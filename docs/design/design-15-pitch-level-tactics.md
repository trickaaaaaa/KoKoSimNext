# 設計書15 — 1球単位の試合進行と打席内采配

> 現状の試合進行は **打席単位**（`GameStep.PlateAppearance` の粒度）で、UIが出している「1球ごとのB/S」は
> 打席結果確定後に辻褄合わせで生成した**架空の合成列**（`PitchSequenceSynthesizer`）である。
> 本書は、これを **エンジンが実際に解いている1球ごとの結果** に置き換え、さらに **打席の途中（球と球の間）で采配**（サイン・伝令・牽制・盗塁・敬遠）を挟めるようにする「1球単位化（重）」の設計と段階移行を定義する。
>
> 設計思想の背骨は一言で: **「AtBatを再開可能な投球ステッパに作り替え、`GameStep` に `Pitch` 境界を足す。無指示なら結果は今日と1ビットも変わらない」**。
> 采配（指示）だけが結果を動かし、観測（1球データの露出）は結果を変えない。これは設計書12「観測は試合結果を1ビットも変えない」の采配版である。

関連: design-01（打席解決6段階）, design-09（試合采配・サインは1球ごと無制限）, design-11（敵AI三層）, design-12（詳細プレー表現・観測seam）, design-14（投球ループ内イベント＝暴投/パスボール, 乱数順の既定オフ）。

---

## 0. 決定事項（2026-07-19・本書起票時に確定）

| 論点 | 決定 | 含意 |
|---|---|---|
| **1球データ(PitchRecord)をdigestに含めるか** | **含める** | digest正規化文字列に1球フィールドを追加＝**ハッシュを一度だけ再ベースライン**。決定論ゲート（同シード→同ハッシュ）は維持。得点分布＝統計帯は不変（1球データはRNGから決定論的に導出されるだけ） |
| **敵AI・委任采配を1球粒度に上げるか** | **上げる** | AI/委任 brain が1球ごとに読み合う。**結果が実際に変わる＝統計帯が動く**。帯再校正＋digestカード再ベースラインを伴う（Phase C） |
| **詳細モードの停止粒度** | **打席頭のみ＋手動一時停止** | 進行の自動停止は従来通り打席頭。プレイヤーは打席の途中で**任意に一時停止**して1球采配を差し込む。全球で自動停止はしない（UI原則⑦「操作は少なく深く」） |

### 0.1 Q12 残未決の解決（2026-07-19・追決定）

| 論点 | 決定 | 反映 |
|---|---|---|
| **Q12-1 弾道を判定に噛ませるか** | **将来 Phase E で噛ませる**（今は観測専用） | §6 に Phase E を追加。B〜D は弾道=観測専用で帯不変、E で弾道→swing/contact判定に接続＝**全帯再校正**。不変条件#1（二層構造）への最終的な忠実化 |
| **Q12-2 手動停止の割り込み口** | **全球で采配ショートカット常駐** | 進行は自動停止しない（打席頭のみ）が、各球に1球采配のショートカットを**常時**表示。プレイヤーはどの球でも割り込める。※UI原則⑦「操作は少なく深く」との密度調整は Phase C の3案で捌く |
| **Q12-3 方針×1球指示の優先** | **1球指示が方針を単純上書き**（§2.3 の通り） | 方針は状態を持たず「指示が無い球のデフォルト」。競合は1球指示が勝ち、次球は方針に戻る |
| **Q12-4 バント/スクイズ統一** | **Phase D で統一** | 独立ミニループを統一ステッパへ載せ替え（暴投/パスボールも同境界）。帯再校正1回込み |
| **Q12-5 Trajectory のスコープ** | **Phase B から載せる** | PitchRecord に `Trajectory` を Phase B で含める（観測専用）。`PitchSimulator` 毎球呼び出しのコスト検証を Phase B の作業に含める |

未決の残件は OPEN-QUESTIONS.md **Q12** を参照（本追決定で主要論点はクローズ）。

---

## 1. 現状の要点（設計の前提）

- 打席解決 `AtBatResolver.ResolveDetailed`（`Match/AtBat/AtBatResolver.cs:47`）は**既に1球ずつ回る密ループ**。配球→散布→打者判断→コンタクト→守備の順にRNGを消費する。だが**返り値は打席結果（enum）＋総球数に潰れ、1球ごとの中間結果は捨てられる**。
- 采配の入口 `ITacticsBrain`（`Match/Tactics/ITacticsBrain.cs:11`）は全メソッドが「**打席頭で1回**」呼ばれる。1球ごとの呼び出しは存在しない。入力型 `TacticsSituation` に**カウント（B/S）・球数・直前の球の結果が無い**。
- 盗塁・バント・スクイズ・敬遠・牽制は `GameEngine.PlayHalfSteps`（`Match/Game/GameEngine.cs:206`）内、**AtBatResolverの外**で打席前後に処理される。バント/スクイズは各々独立に球を回すミニループを持つ（`ResolveBuntSign` L749 等）。
- 進行イテレータ `GameEngine.Steps`（L111）は**打席境界でのみ yield**。`GameStepKind` は `PlateAppearance` 1種のみで、`Pitch`/`Timeout` は**コメントで予約済み**（L890-903, L106-110, L216）。**yield は乱数を消費しない**＝batch `Play` と対話進行が同一結果。
- 決定論ゲート: `GameResultDigest`（Tests側）が `GameResult` 全体（スコア/全ログ/全統計/全カウンタ）をSHA256で正規化。カード `avg/tactics/modern × seed 1..50`。RNGは `IRandomSource` 注入・`Fork` で並列部分和（Balance側）。

**設計的含意**: 境界拡張点は1箇所（`GameStepKind`）に集約済み。中核作業は「密ループを状態外出しした再開可能ステッパへ変換」する1点に絞れる。RNG消費順を触らなければ帯は不変。

---

## 2. 中核アーキテクチャ

### 2.1 AtBatを「再開可能な投球ステッパ」に

`AtBatResolver.ResolveDetailed` の密ループを、状態を外に持つ **`AtBatSession`**（ステートマシン）へ作り替える。ループ制御を呼び出し側（GameEngine）に外出しするが、**1球内のRNG消費順は現行と完全一致**。

```
session = AtBatSession.Begin(context)          // count=0-0, 球数0, 走者/rattled を保持
loop:
    directive = <その1球への采配 or null>       // swing/take/bunt/steal/pitchout/pickoff/IBB/配球方針
    resolution = session.ThrowNextPitch(directive, rng)   // 1球だけ解く
    if resolution.EndsPlateAppearance: break
return session.Result                          // 従来と同じ PlateAppearanceResult＋PitchLog
```

- `directive == null`（無指示）なら、`ThrowNextPitch` は**現行ループの1イテレーションと同じ分岐・同じRNG順**を辿る。
- session が保持: `balls/strikes/pitchCount/走者/PitcherRattled/rattled窓/伝令窓`。従来 `AtBatResolver` のローカル変数を state に昇格させるだけ。
- `ThrowNextPitch` は**1球分の実結果** `PitchResolution`（球種・狙い/着弾コース・球速・スイング可否・PitchKind・カウント後・インプレー時の打球）を返す。従来は捨てていた値をそのまま返すだけ。

> **不変条件**: `directive` を常に null で回した `AtBatSession` の帰結は、`AtBatResolver.ResolveDetailed` と**打席結果・RNG消費・球数まで完全一致**。これを単体テストで固定（Phase A のDoD）。

### 2.2 `GameStep` に `Pitch` 境界を追加

`GameStepKind` に `Pitch`（1球ごとの采配窓）と `Timeout`（伝令窓）を足す（既に予約コメントあり）。`PlayHalfSteps` の打席内で、各投球の**前**に `yield return Pitch(...)` を挟む。

```
while (outs < 3):
    ...（打席頭の従来サイン処理は温存: CallOffense/CallDefense/伝令）
    session = AtBatSession.Begin(...)
    loop:
        yield return Pitch(...)                 // ← 采配窓（乱数を消費しない）
        directive = collectDirective(...)       // batch=null / AI=CallPitchAction / 手動=一時停止入力
        res = session.ThrowNextPitch(directive, rng)
        if res.EndsPlateAppearance: break
    yield return Pa()                            // ← 従来の打席境界（据え置き）
```

- **batchモード**（`Play` が drain）: 窓で何も返さない → directive=null → **今日と一致**。
- **yield は乱数を消費しない**という現行の性質を1球境界へ拡張。これが決定論の要。
- `Pa()` 境界（打席末）は**そのまま残す**。replay/保存/委任再現（`GameReplay` / `MatchProgression`）の既存の打席単位境界を壊さない。

### 2.3 `ITacticsBrain` に1球メソッド（optional interface）

打席頭メソッド群はそのまま。**別インターフェース** `IPitchTacticsBrain`（実装は任意）を足す。

```csharp
public interface IPitchTacticsBrain {
    // その1球への上書き指示。null=方針まかせ（RNGを1発も引かない）
    PitchDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng);
}
```

- `PitchTacticsSituation` = 既存 `TacticsSituation` ＋ `Balls/Strikes/PitchNumber/LastPitchOutcome/走者スタート状況`。
- default interface method ではなく**別I/Fにする理由**: 「実装していない brain は窓を開かず**RNGを1発も引かない**」を型で明快に保証するため（`brain is IPitchTacticsBrain pb` の分岐で、非実装なら Fork も生成しない）。
- **方針×1球指示の優先**（design-09 §1）: 打席頭 `CallOffense` が方針、`CallPitchAction` が1球上書き。「1球指示は方針より常に優先／方針は指示しない球に効く」。

---

## 3. 決定論・統計帯の担保

### 3.1 三本柱

| 柱 | 内容 | 効果 |
|---|---|---|
| **no-opゲート** | `IPitchTacticsBrain` 非実装 or directive=null なら投球窓でRNG不消費 | 無指示経路が今日とバイト一致。Phase A/B の帯・分布が不変 |
| **Fork隔離** | 1球判断のRNGは `rng.Fork(pitchStreamId)` で分離し主RNGを進めない | AI/手動の判断が本流の投球解決順を乱さない（design-14パターン） |
| **既定オフ係数** | pitchout/牽制/暴投など新プレーは係数0でRNG非消費 | `GameEngine.cs:333/363/666` の踏襲。段階導入で従来一致 |

### 3.2 digest（決定PitchRecordを含める）の扱い

- `GameResultDigest.Canonical` に **1球フィールド（PitchLog）を追加**する。
- 影響: **ハッシュ値が一度だけ変わる**（＝再ベースライン）。決定論ゲート「同シード→同ハッシュ／batch==manual／中断再開==全消化」は**そのまま成立**（1球データは同じRNGから決定論的に導出）。
- **統計帯（`balance-targets.yaml` の得点/HR%等の分布）は不変**。digestはハッシュ（再現性）、帯は分布（バランス）で別物。1球データ追加は分布を動かさない。
- 実務: PitchRecord を Canonical に載せた時点（Phase B）で、pinされたハッシュを**一度スナップショット再取得**。以降は固定。

### 3.3 AI 1球化で帯が動く点（Phase C）

- 敵AI・委任を1球粒度に上げると、AIが実際に読み合って**結果が変わる＝統計帯が動く**。これは設計意図（1球采配は結果を動かす）。
- 従って **Phase C は帯再校正イベント**: `balance-targets.yaml` の再校正＋digestカード（特に `tactics`）の再ベースライン＋新カード（`pitch-tactics`）追加。
- Fork隔離により**本流の投球解決順は保たれる**が、AIが選んだ directive 自体が分岐を変えるため分布は動く。「無指示（旧brain）は不変／1球brainは新ベースライン」を明示的に分けてテストする。

---

## 4. 観測seam：架空合成 → 実データ

- `PitchSequenceSynthesizer`（`Match/Timeline/Playback/PitchSequence.cs` の**架空合成**）を、`AtBatSession` が吐く**実 `PitchRecord` 列**に置換。
- `PlayLogEntry` に `PitchLog: IReadOnlyList<PitchRecord>?` を追加。**digest対象**（§3.2の決定）。
- `PitchRecord` フィールド（案）: `Kind(PitchKind) / BallsAfter / StrikesAfter / PitchType / LocationX / LocationY / VelocityKmh / Trajectory?(観測専用)`。
- `MatchProgression.LivePlateAppearance.PitchSeq` を実データ供給に。UI（`MatchLiveController` は既に1球点灯を実装済み）は**接続最小**。
- 弾道: 完成済みだがデッドの `PitchSimulator`/`PitchTrajectory`/`PitchSpec`（`Match/Pitching/`、現状テストからのみ参照）を `AtBatSession` が毎球呼び `Trajectory` に載せる（**Phase B から**）。B〜D は**判定に噛ませず観測専用**（帯不変）。判定に噛ませるリアル化は **Phase E**（§6・§0.1 Q12-1）で全面再校正とセットに行う。

---

## 5. 打席内の既存プレーの扱い（段階移行）

バント/スクイズ/盗塁/牽制/敬遠は現状 GameEngine 内で独立に処理され、バント/スクイズは既に独自の球ループを持つ。これらを一気に統一ステッパへ載せ替えるとRNG順が変わり**帯が動く**ため、**Phase A〜C では温存**（新ステッパの外・従来経路のまま）。統一は Phase D で帯再校正とセットにする。

---

## 6. フェーズ計画（DoD）

| Phase | 内容 | 帯/digest | DoD |
|---|---|---|---|
| **A 土台** | `AtBatSession` ステッパ化＋`GameStep.Pitch` 追加＋no-op窓。brain はまだ打席頭のみ | **どちらも不変** | 既存 digest カード（avg/tactics/modern×50）**全一致**。`Session(null)==ResolveDetailed` 単体テスト緑。UI無変化 |
| **B 実データ露出** | PitchRecord 露出（`Trajectory` 含む・観測専用）、`PitchSequenceSynthesizer` 置換、UI接続。digest に PitchLog 追加。`PitchSimulator` 毎球呼び出しのコスト検証 | 帯不変／**digestは一度再ベースライン** | 1球ごとに実B/S・球種・コース・球速・弾道が出る。得点分布・全統計は Phase A と一致。ハッシュのみ更新スナップショット |
| **C 1球采配** | `IPitchTacticsBrain` を Standard/Ai に実装（AI 1球化）＋プレイヤー手動采配（**全球で采配ショートカット常駐**）。Fork隔離 | **帯が動く**＝再校正 | 無指示経路は不変を維持。`tactics` 再ベースライン＋新カード `pitch-tactics`。`balance-targets.yaml` 再校正。UI采配ショートカットは3案提示→選択後に実装（密度は UI原則⑦ で調整） |
| **D 統一・拡張** | バント/スクイズ/盗塁/牽制/暴投を統一ステッパへ載せ替え。design-14 の投球ループ内イベント（暴投/パスボール）接続。**真の1球進行**（Q12-7決定）: `MatchProgression`/`GameReplay` に「次の1球まで進める」モードを追加し、手動1球指示がどの球にも届くようにする（C-3の「次打席初球予約」を置換） | **帯が動く**＝再校正 | Heavy 実測→帯着地→固定値再スナップショット。design-14 の該当プレーを1球境界で発火。手動指示が打席内の任意の球に効くことを保存/再現込みでテスト |
| **E 弾道判定（完了 2026-07-20）** | Phase B で観測用に載せた `Trajectory`（マグヌス変化量・到達時間）を **swing/contact 判定に接続**。ランク直参照ベースの現判定を弾道ベースへ置換 | **全帯が動く**＝全面再校正 | 不変条件#1（二層構造）への最終忠実化。`ContactModel`/`BatterDecision` を弾道入力へ改修。E-1（特徴量基盤・テーブル補間）/E-2（空振り置換）/E-3（打者判断接続）/E-4（締め）まで全スライス完了。詳細は下記 Phase E 完了注記 |

**着手順の原則（CLAUDE.md）**: 各Phaseはテストから書く。Phase A は「stepper==従来」の固定テストが最初の1本。UI（Phase C の采配窓）は ASCII ワイヤーフレーム3案提示→選択待ち（UI-BUILD-METHOD.md）。

**Phase D 完了（2026-07-20）**: D-1（真の1球進行）〜D-4（帯再校正の締め）まで全スライス完了。バント/スクイズ/盗塁/牽制/暴投/パスボールを統一ステッパへ載せ替え済み、打順消費タイミングの是正（旧Q13）も同期間に解決。Heavy 20/20・非Heavy 633（Skip 1含む）で全緑、digestは各スライスごとに理由を特定した上で再ベースライン（`avg`/`modern` カードは全スライスを通じて無指示経路の不変を維持）。詳細な実測ビフォーアフターは `HANDOFF-2026-07-19-pitch-level-phaseD.md` §8 を参照。

**Phase E 着手決定（2026-07-20・ユーザー判断）**: Q12-1 の予約どおり弾道→swing/contact 判定接続に着手する。引き継ぎ・段階計画は `HANDOFF-2026-07-20-pitch-level-phaseE.md`。最重要制約は**統計シムの性能**（毎球RK4積分は Phase B 実測で不可＝特徴量の事前計算/近似が必須）と**モデル設計の事前合意**（ContactModel/BatterDecision をどう弾道入力化するかは案を提示して合意後に実装）。

**Phase E-1 完了（2026-07-20）**: 性能方式は**事前計算テーブル＋バイリニア補間**（ユーザー承認）。誘発縦変化・到達時間は (球速, rpm) の2変数だけの純関数（リリース角/方位角に非依存）という性質を利用し、`TrajectoryFeatureTable`（`Match/Pitching/`）が起動時に一度だけ格子（70–176km/h×2刻み／1800–2700rpm×50刻み）をRK4で埋め、毎球はO(1)バイリニア補間で参照する。積分器 (`PitchSimulator`/`BallisticIntegrator`) はゴールデン照合の参照実装として温存（`TrajectoryFeatureTableTests`）。`AtBatSession.LastPitchFeatures` として毎球参照可能にしたが、**まだどの判定にも接続していない**（無使用の参照のみ）。実測: Balance CLI `simulate-games --games 10000 --tactics` のレポートが特徴量参照の有無で完全一致（バイト単位）、CPU時間は146.6s→147.5s（+0.6%、目標「2倍以内」に対し実質無視できる差）。`!Heavy 644`（新規12件追加）・`Heavy 20/20` 全緑、digestベースライン再取得不要（1ビットも変化せず）。
モデル接続の合意事項（ユーザー承認・2026-07-20）: **loc（ボール/ストライク判定位置）の生成方法は変えず**、弾道由来の変化量・到達時間を独立した「欺瞞度」スカラー特徴量として既存のゾーン判定へ上乗せする方式（ControlScatterの散布モデルと弾道積分の着弾点を完全統合する案は、影響範囲が大きいため今回は見送り、将来課題として残す）。次は E-2（空振りモデル置換・全帯再校正）。

**Phase E-2 完了（2026-07-20）**: `ContactModel.Resolve`/新設 `ContactModel.WhiffProbability` の空振り対数オッズを、旧 `(PitchRank−50)×StuffPerPitchRank`（球種ランク直参照）から**弾道由来の誘発変化合成量**（`PitchTrajectoryFeatures.BreakMagnitudeM = sqrt(縦²+横²)`、係数 `WhiffBreakSlope`）へ**完全置換**（ユーザー承認: 混ぜない・完全置換。Sharpness→rpm→変化量の経路で既にPitchRankが弾道へ織り込み済みのため二重計上を避ける）。到達時間項は見送り（球速項と共線になるため、1スライス1変更の原則でE-2の変更を「rank項→合成変化量項」の1変数に絞った。ユーザー承認）。物理妥当性テスト（`ContactModelTests`）: 合成変化量↑→whiff確率の単調増加、無回転相当（変化量≈0）が帯の最低水準になること、縦のみ/横のみで合成量が同じなら同確率になること、ゾーン外がゾーン内より空振りしやすいこと、を固定。
係数校正（Balance CLI `simulate-games --games 10000 --tactics`, seed42/2024・no-tactics seed42）: `WhiffIntercept` を `-1.68→-2.40` に再校正（`WhiffBreakSlope=1.5` は初期見積りのまま）。実測: 三振19.5%/19.5%・四球8.21%/8.24%・本塁打2.81%/2.85%・得点4.05/4.11（tactics）、三振20.2%・四球8.8%・本塁打2.8%・得点4.17（no-tactics）で全て `balance-targets.yaml` 帯内。`!Heavy 648`（新規4件 `ContactModelTests`）・`Heavy 20/20` 全緑。digestは想定通り**全4カード×50シード（avg/tactics/pitch-tactics/modern）が再ベースライン**（空振り判定は無指示経路も含め全打席が通るため、Phase C以来はじめて `avg`/`modern` カードが動いた。想定通りと確認済み）。E-4の締め報告には球種別空振り率のビフォーアフターを含める（ユーザー指定）。

**Phase E-3 完了（2026-07-20）**: `BatterDecision.DecideSwing` を `SwingProbability`（テスト用に分離）へ拡張し、`PitchTrajectoryFeatures.BreakMagnitudeM` を**単一係数 `ChaseBreakSlope` の符号反転**で対称に効かせた（ゾーン外+＝釣られる／ゾーン内−＝見誤って見送る、ユーザー承認の1変数原則）。追加で `StrikeZone.DistanceOutsideM`（ゾーン外への距離）と係数 `ChaseDistanceSlope` を新設し、ゾーン外側にのみ距離減衰を掛けた（ゾーン内は距離=0で常に無効なので対称性は崩れない）。**設計時の発見**: 着手前は「既存の距離減衰構造を保存する」という前提だったが、実際の `DecideSwing` は inZone 二値のみで距離減衰自体が存在しなかった。ユーザーに確認の上、本スライスで `StrikeZone.DistanceOutsideM` を新設して初めて距離減衰を導入した（「保存」ではなく「新設」）。物理妥当性テスト（`BatterDecisionTests`）: 対称性（ゾーン内外の変化量デルタが符号反転のみで一致）、ゾーン外距離に対する非増加（クランプ域では等号、非クランプ域では厳密減少）、固定距離での変化量↑→チェイス確率↑、ゾーン内は距離が無関係、を固定。
係数校正: `ChaseBreakSlope`（新設・初期見積り0.15）と `ChaseDistanceSlope`（新設・初期見積り0.6）の2ノブが強く絡み合う（distanceガードが弱いとBB↓＋K↑寄り、strongいとBB↑寄り）ため、両方を実測しながら追い込んだ。最終値: `ChaseBreakSlope=0.02` / `ChaseDistanceSlope=0.15`（`WhiffIntercept`等E-2の値は再校正不要でそのまま）。実測（Balance CLI, 10000試合）: 三振19.83%/19.88%・四球8.59%/8.59%・本塁打2.79%/2.79%・得点4.09/4.06（tactics, seed42/2024）、三振20.57%・四球9.31%・本塁打2.75%・得点4.19（no-tactics, seed42）。全て帯内。既存切片（ChaseBase/ZoneSwingBase）は変更不要だった。`!Heavy 652`（新規5件 `BatterDecisionTests`）・`Heavy 20/20` 全緑。digestは想定通り全4カード再ベースライン。
**副産物の発見（Phase Eと無関係の既存挙動）**: `TimelineTests.Timeline_FormationAssignsRoles_IncludingFieldBall`（seed固定13）が、E-3のRNG分岐シフトで偶然「実捕球者の移動距離<0.5mだとFieldBallムーブ自体を出さない」という`TimelineBuilder.BuildBattedBall`の既存仕様（コンバッカー等、バグではない）を初めて踏み、テストの「全打球にFieldBallを要求」という過剰に厳しいアサーションが破綻した。テストを「1試合中に最低1回FieldBallがあれば陣形機構は機能している」という本来の意図に合わせて緩和して解決（本番コードは無変更）。

**Phase E-4 完了・Phase E クローズ（2026-07-20）**: 締めの実測確認・ビフォーアフター報告。詳細レポートは `out/phaseE4-report.md`。
- **球種別集計の新設**: `GameSimulation.Stats` に球種別の空振り率・チェイス率を追加（`SwingsByPitchType`/`WhiffsByPitchType`/`OutOfZoneByPitchType`/`ChasesByPitchType`、enum添字の整数集計＝並列マージ順に依らず決定論）。**D-4の教訓を踏襲し集計は Balance 側に完全に閉じた**（`PitchLog` は `PlayLogEntry` から読むだけ）ため、engine側 record の `ToString()` 経由の digest 一律シフトは発生せず、digest 完全不変（再ベースライン不要）を実測確認。
- **ビフォーアフター（Phase D の旧モデルを一時再現して同一集計器で採取）**: 旧モデルは Phase D の K/BB/HR（19.12%/8.21%/2.84%）を1桁まで完全再現し、退避の忠実性を確認。**空振り率プロファイル**が本フェーズの核心: Before は3球種とも平坦（21.3–21.5%、ランク直参照＋rawVelo が球種非依存のため差が出ない）→ After は**変化球（スライダー/フォーク）がストレートを +1.0〜1.3pt 上回る**（球速比の低い変化球ほど滞空時間が伸び誘発変化量が増える二層構造が判定に反映）。チェイス率は Before から既に変化球が約1pt高く、これは E-3 でなく Phase C の1球采配（決め球の変化球多投）由来の既存効果（no-tactics ではほぼ消える）で、E-3 の `ChaseBreakSlope=0.02` の寄与は穏やか。
- **最終確認**: 10000試合×seed42/2024（`--tactics` あり/なし）で K/BB/HR/得点すべて `balance-targets.yaml` 帯内。`!Heavy 652`・`Heavy 20/20` 全緑。
- **置換式の最終係数**: `whiff_intercept=-2.40` / `whiff_break_slope=1.5`（旧 `stuff_per_pitch_rank=0.012` を置換）/ `chase_break_slope=0.02` / `chase_distance_slope=0.15`。旧 rank 直参照は完全廃止し、球種の効きを Sharpness→rpm→弾道積分→誘発変化量 の物理経路に一本化（不変条件#1への最終忠実化）。

**これで設計書15の全フェーズ（A/B/C/D/E）が完結した。** 1球単位の試合進行・打席内采配・弾道由来の swing/contact 判定まで、当初計画（§6）の全 DoD を満たして着地。

---

## 7. リスクと対策（要約）

- **密ループのstate外出しでRNG順が微妙にズレる** → Phase A の `Session(null)==ResolveDetailed` 差分テスト（打席結果＋消費RNG数＋球数）で機械的に検出。ズレたら即赤。
- **digest再ベースラインの取りこぼし** → PitchLog を Canonical に載せる commit と、ハッシュ再スナップショットを同一commitに束ねる。
- **AI 1球化の帯暴れ** → Phase C を独立フェーズに切り、無指示経路の不変（回帰）と1球経路の新ベースライン（再校正）を別テスト群に分離。
- **手動一時停止のUX過負荷** → 全球停止はしない（決定事項）。停止条件・导線は Phase C の UI 3案で詰める（OPEN Q12）。

# 引き継ぎ — 設計書15 Phase D（統一ステッパ化＋真の1球進行）2026-07-19 → 更新

> 別チャットで **設計書15 Phase D の残り（D-2c以降）** に着手するための引き継ぎ文書。
> D-1/D-2a/D-2b はこのチャットで完了済み。まず本書全体（特に §0〜§3）を読んでから始めること。
> `docs/design/design-15-pitch-level-tactics.md`（§5/§6/§0.1）・design-14 も併読必須（元の指示のまま）。

## 0. 前提（Phase A/B/C + D-1/D-2a/D-2b 完了済み・2026-07-19）

- **A/B/C**: 前回の引き継ぎ通り完了済み（`AtBatSession` ステッパ・実 `PitchRecord`・`IPitchTacticsBrain` C-1 seam・AI 1球化・手動采配UI）。
- **C-3 UIバッチスクショ7箇条自己レビュー**: 実施済み・**違反なし**（トークン/部品辞書のみ使用、既定無点灯を確認）。持ち越しはクローズ。
- **D-1（真の1球進行）完了**: `MatchProgression.AdvancePitch()` + `AdvancePitchResult{Pitch,PlateAppearance,Finished}` + `PendingPitchIndex` を新設（従来 `Advance()` は無変更のまま温存）。`GameDecision.PitchIndex`（既定0=打席頭＝旧セーブ互換）を追加。`GameReplay.Restore` を PA添字のみ→ (PA添字, PitchIndex) の球単位ループへ書き換え。**帯・digestともに完全不変**。
- **D-2a（敬遠の統一）完了**: `AtBatContext.IntentionalWalk`(bool) 新設・`AtBatSession.ThrowNextPitch` 先頭で判定し投球数0・RNG非消費のまま即Walk確定。**digestが1ビットも動かなかった**（Heavy 20/20全一致、Fork隔離のおかげ）。
- **D-2b（バントの統一）完了**: 送り/セーフティバントを `AtBatSession` へ統一。`PitchBattingOverride.Bunt/SafetyBunt` 追加、`AtBatSession.Begin` に `Player? batterPlayer=null`（末尾・省略可）追加、`AtBatContext.Baserunning` 追加、`AtBatResult.BuntOutcome` 追加。**2ストライク到達後は実カウントを保ったまま強攻へフォールバックする**ように改善（旧: カウントを0-0にリセットしていた簡略化を解消）。実装中に **brain の1球指示クエリが無条件代入で非null既定を握りつぶす latent bug** を発見・修正（詳細は §4 参照、D-2c/D-2d でも同じ罠に注意）。digest再ベースライン実施（`pitch-tactics`/`tactics` が変化、`avg`/`modern` は完全不変）。Heavy帯は**再校正不要**（既存の広め帯内に着地）。新規テスト `BuntUnificationTests.cs`。
- **D-2c（スクイズの統一）完了**: スクイズを `AtBatSession` へ統一。`PitchBattingOverride.Squeeze` 追加、`AtBatSession.Begin` に `Player? thirdBaseRunner=null` / `double squeezeWasteProbability=0.0`（末尾・省略可）追加、`AtBatResult.Squeeze`（`SqueezeOutcome?`）追加、`PitchResolution.SqueezeRunnerCaughtAtThird`（bool、既定false）追加。結果判定は指示通り `SqueezeResolver.Resolve` のまま変更していない（ウエスト確率は GameEngine が打席頭で1回だけ計算し `Begin` へ渡す）。design-02 §4.4 通り**1球固定**（バントと違い2ストライク未満の継続方針にしない）なので、`battingOverride=Squeeze` は `session.PitchCount==0` の最初の1球にしか渡さない。
  - **意味論の訂正（実装前の誤報告）**: 着手前に「Foul/MissedBuntで送りバントが不成立になった場合、既存コードはbases.Thirdクリア漏れのバグがある」と誤って報告しユーザー承認を得たが、**実装・テストの過程で誤りと判明**。`SqueezeOutcome` は `RunnerOut=true` のとき常に `BatterOut=false, Runs=0` の同一タプルを返し、これは「ウエストを読まれた」場合と「送りバント自体がFoul/MissedBuntで不成立だった」場合の**両方**で発生する。旧コードの分岐 `if (!sq.BatterOut && sq.Runs==0 && sq.RunnerOut)` は理由を問わずこの2ケースを**同一に**（`bases.Third=null; outs++;` して打席続行）扱っており、**バグは存在しなかった**。新実装もこの意味論をそのまま `PitchResolution.SqueezeRunnerCaughtAtThird` で保存している（`res.Squeeze` に来るのは常に `SacrificeSuccess/InfieldHit/PopOut` の3種のみ＝そこでの `RunnerOut` は常にfalse）。次に似た「既存バグ発見」を報告する前に、SqueezeOutcomeのような「複数の原因が同じ結果タプルに畳み込まれる」設計を疑ってから確定させること。
  - **digest**: 再ベースライン手順を実施したが **200行（avg/tactics/modern/pitch-tactics×50seed）が1バイトも変化しなかった**（`determinism-baseline.txt` 更新不要）。`StandardTacticsBrain`/`AiTacticsBrain` のスクイズ条件（`SqueezeFromInning`/`SqueezeMaxDiffAbs`/`SqueezeMinBunt`/確率）が、既存4カード×50シードのサンプル内で一度も発火しなかったため（バントより遥かに稀な条件）。Heavy帯も**再校正不要**（20/20緑）。
  - 新規テスト `SqueezeUnificationTests.cs`（`Match/AtBat/`）。GameEngineレベルは `AlwaysSqueezeBrain` で複数シード試合を最後まで走らせ、企図記録＋塁状況破綻が無いことを確認。
- **D-2d（盗塁/牽制/重盗の統一）完了**: A案（球間イベント化＝カウント非消費のまま毎球発動可能に）で決着。盗塁の「試みるか＋始動種別」の判断を、打席頭一度きりの `ITacticsBrain.CallOffense`/`CallStartType`（後者は削除）から、毎球の `IPitchTacticsBrain.CallPitchAction`（新設 `PitchTacticsDirective.StealAttempt: StartType?`）へ全面移動。`GameEngine.cs` の毎球ループ内、`battingOverride`/`pitchOverride` と同じ場所で問い合わせ、解決自体（`PickoffResolver`→`StealReadModel.RollPitchout`→`StealResolver`→重盗ロールの呼び出し順）は不変のまま移設。`StandardTacticsBrain`/`AiTacticsBrain` は成功見込み判定・ギャンブル判定・ティアゲート（`StealMinTier`/`GambleStartMinTier`とは独立の `PitchTacticsMinTier`）・misfire（無謀盗塁, `RecklessOnMissProb`）をすべて毎球版へ移植（解決式・係数は不変）。3アウト目で打席が未決着になるケースは D-2c の `squeezeAbandoned` と同型の `stealEndedHalf` で処理。
  - **digest**: 今回は**帯が動いた**（想定通り）。4カード×50シード中、`pitch-tactics` カードのみ41/50シードでハッシュ変化（`avg`/`tactics`/`modern` は完全不変＝brain無し経路・打席頭1球采配のみ経路は無傷）。再ベースライン実施済み。**Heavy帯は20/20緑で再校正不要**（発動タイミングの多様化のみで、盗塁企図率・成功率自体は既存の広め帯内に着地）。
  - **副産物の発見**: `squeezeAbandoned`/`stealEndedHalf`（3アウト目で打席未決着のまま終わる経路）は `offense.NextBatter()` の**後**で発火するため、次イニング先頭になるのは中断された打者ではなく**次の打者**（コード内コメントの記述「この打者は次イニング先頭」と実際の挙動が1人ズレて食い違う）。D-2c時点で既に存在した挙動で、今回スコープでは独自に「修正」せず D-2c と同じ挙動へ盗塁死・牽制死を素直に揃えた。詳細・要判断は `OPEN-QUESTIONS.md` Q13。
  - 新規テスト `StealUnificationTests.cs`（`Match/AtBat/`）。`AlwaysStealBrain` の複数シード完走確認に加え、`DelayedStealBrain`（打席3球目以降でしか試みない）で「旧アーキテクチャでは到達不能だった打席途中の発動」を固定。
- **D-2e（打順消費タイミングの是正・旧Q13）完了**: `GameEngine.PlayHalfSteps` の `offense.NextBatter()` を、`AtBatSession.Begin` 直前（投球ループへ入る前）から `squeezeAbandoned`/`stealEndedHalf` ガード直後（打席が実際に確定した時のみ通過する1箇所）へ一本化。3アウト目で打席未決着のまま終わる打者（スクイズ挟殺・盗塁死・牽制死）は、次にこの打者が来る時（＝次イニング先頭）に必ず同一人物が立つ（次打席は新規`AtBatSession`のためカウントは自動的に0-0）。**副産物**: 犠打/スクイズが決着する打席でも旧実装は`NextBatter()`を二重に呼んでおり（Begin前1回＋決着後の分岐内でもう1回）、決着のたびに打者を1人分余計に飛ばしていたバグも同じ修正で解消（盗塁/牽制には存在しなかった、バント/スクイズ決着分岐特有のバグ）。新規テスト `BattingOrderContinuationTests.cs`（`Match/Game/`）: (1)打順の完全周期性、(2)全打席が0-0の新規カウントから始まること、(3)保存/復元/続行(`GameReplay`)が中断なし実行と完全一致すること、を固定。
  - **digest**: `avg`/`modern`は完全不変。`tactics`22/50・`pitch-tactics`15/50が変化（打順が変わったことで以降の打者依存の采配判断が連鎖的にずれるため想定通り）。再ベースライン実施済み。**Heavy帯は20/20緑で再校正不要**。
  - `OPEN-QUESTIONS.md` Q13はクローズ済み。
- **D-3（暴投・パスボール, design-14 P2-8）完了**: 走者ありの各投球のうち実際にキャッチャーへ到達/通過した球（`session.LastPitchKind` が `Ball`/`CalledStrike`/`SwingingStrike`。`Foul`/`InPlay`は対象外）だけを対象に、毎球ループ内（`session.ThrowNextPitch`直後・スクイズ挟殺処理の直後）で低確率のバッテリーミスを判定する。発生時は `BaserunningModel.ApplyBatteryMiss`（全走者1つ進塁・三塁走者は生還する純関数）を適用。暴投=投手責/パスボール=捕手責は意味上分けるが記録は合算1カウント（`GameResult.WildPitchCount`）に簡略化（design-14の割り切り通り）。新係数 `BaserunningCoefficients.WildPitchProb/WildPitchControlSlope/PassedBallProb/PassedBallCatchingSlope`（既定0＝機能オフ）。
  - **踏んだ地雷（要記憶）**: 初回実装でゲートを「計算後（傾き適用後）の確率が正」で書いてしまい、既定係数0でも投手Controlや捕手Catchingが50未満（母集団の約半数）だと傾き項がベース確率を押し上げてrngを消費してしまうバグを作った→digestが`avg`/`modern`含む全カードで48-49/50シード崩壊して即発覚。`DropThirdStrikeReachProb`/`PickoffBaseProb`と同じ「生の基準係数が0より大きい時だけ分岐に入る」パターンへ修正（計算後の値でゲートしない）。
  - 係数投入: `wild_pitch_prob=0.0012`/`wild_pitch_control_slope=0.0006`/`passed_ball_prob=0.0008`/`passed_ball_catching_slope=0.0006`（Balance CLI `simulate-games`実測≈0.56-0.59/試合・両軍計、seed42/2024）。`data/balance-targets.yaml`に`wild_pitch_per_game: {min:0.15, max:1.00}`を新設（`games_10k`セクション＝Brain不要の常時系）。得点/チーム等の既存帯は無変化（4.1台のまま）。
  - **digest**: 完全不変（`DeterminismCards.Run`は`new GameContext()`＝`BaserunningCoefficients`既定値を使うため、`data/coefficients.yaml`側の係数投入はどのカードにも触れない。再ベースライン不要）。
  - **Heavy**: 20/20緑（`wild_pitch_per_game`新帯を含め全帯着地）。
  - 新規テスト: `BaserunningPlaysTests.cs`に追記（`ApplyBatteryMiss`の純関数テスト3件＋GameEngineレベルの既定オフ/有効化テスト2件）。
- **D-4（帯再校正の締め）完了・Phase D クローズ（2026-07-20）**: 詳細は §8。副産物として `TeamState.SacrificeBuntSuccesses`（犠打成功率の集計）を新設し、Balance CLI レポートに追加。この追加自体が `TacticsTally` レコードの `ToString()` を変えるため、digest 4カード×50シードが**全件**動いた（RNG/挙動変化ではなく文字列表現の変化と確認済み）→再ベースライン実施。
- **現在の全緑状態**: `!Heavy 632` / `Heavy 20`。Phase D 全体で `!Heavy` は D-2e〜D-3 時点の632を維持（D-4は新規テスト追加なし、集計項目の追加のみ）。**リポジトリはgit管理外**（コミット単位の記録ではなくメモリ/本書で追跡）。

## 1. 何をやるか（残り・1行）

**Phase D は完了。** 次は E（弾道判定）着手是非をユーザーが別途判断する（Q12-1）。

## 2. 残タスクの持ち越し

なし。D-1〜D-4すべてHeavy/非Heavyとも全緑・digest再ベースライン済みで完了。次のチャットは Phase E の着手是非をユーザーに確認するところから始めてよい（§6参照）。

## 3. 作業ブロック（残り・推奨順）

### D-2c: スクイズの統一（完了・2026-07-19）

§0のD-2c節を参照。`AtBatSession.Begin(thirdBaseRunner, squeezeWasteProbability)` を新設し、GameEngineが打席頭で `waste` を1回計算して渡す設計で決着。`PitchResolution.SqueezeRunnerCaughtAtThird` で「三塁走者だけ挟殺・打席継続」をPA未確定のまま通知し、GameEngine側で `bases.Third=null; outs++` して3アウトなら（盗塁死と同じパターンで）打席を未決着のまま次イニングへ抜ける。「Foul/MissedBuntで不成立なら別扱いすべき」という着手前の想定は誤りで、実際は「理由を問わずRunnerOut=trueなら打席継続」で旧コードと完全に同じ意味論。

### D-2d: 盗塁/牽制/重盗の統一（完了・2026-07-20）

§0のD-2d節を参照。A案（盗塁自体は実球・カウントを消費しない球間イベントのまま、GameEngineの毎球ループの中で任意の球の前に発動可能にする）で実装。`AtBatSession`本体（`Begin`/`ThrowNextPitch`のシグネチャ）は無変更のまま、`GameEngine.cs` 側だけで完結（`PitchTacticsDirective.StealAttempt`を新設し`IPitchTacticsBrain.CallPitchAction`経由で毎球問い合わせ）。`ITacticsBrain.CallStartType` は削除、盗塁の「試みるか」も `CallOffense` から外した（`OffensiveSign.Steal` は握ったまま残置＝タリー等の語彙としては使うが、CallOffenseが返すことはもう無い）。

### D-2e: 打順消費タイミングの是正（旧Q13・完了・2026-07-20）

§0のD-2e節を参照。`OPEN-QUESTIONS.md` Q13の方針(b)を採用し、`offense.NextBatter()`を「打席が実際に確定した時点」（`squeezeAbandoned`/`stealEndedHalf`ガード直後）まで一本化。3アウト目で打席未決着のまま終わる打者が次イニング先頭に正しく立つようになり、副産物としてバント/スクイズ決着時の二重`NextBatter()`呼び出しバグも解消。

### D-3: design-14 投球ループ内イベント（暴投/パスボール）（完了・2026-07-20）

§0のD-3節を参照。既定係数0でRNG非消費から開始→係数投入→Balance CLIで実測→Heavy実測して着地、の順に段階確認。
- 伝令の打席内発動（`GameStepKind.Timeout`）は実装せず現状維持（design-15 §5 の元の方針のまま、着手せず）。

### D-4: 帯再校正の締め（完了・2026-07-20）

- Balance CLI `simulate-games --tactics --coefficients data/coefficients.yaml` を 10000試合×seed42/2024 で実測。全指標が `balance-targets.yaml` の帯内（詳細は §8）。
- 犠打成功率が既存の Balance CLI レポートに存在しなかったため、`TeamState.SacrificeBuntSuccesses`／`TacticsTally.SacrificeBuntSuccesses`／`GameSimulation.Stats.SacrificeBuntSuccessRate` を新設して集計に追加（`BuntResult.SacrificeSuccess` のみを「成功」と数える＝設計コメントの日本語ラベルと一致）。
- この追加で `TacticsTally` の既定 `record` `ToString()` が変わり、digest 4カード×50シードが全件シフト（RNG消費・挙動は無変化、文字列表現のみ）→再ベースライン実施（手順は§4）。Heavy 20/20・非Heavy 632/632 で再確認。
- 実測ビフォーアフターの詳細は §8「Phase D クローズ総括」を参照。

## 4. 決定論の作法（更新: D-2bで得た教訓を追加）

- 載せ替え中も **avg/modern カード（brain無し・小技無し経路）が動く場合は理由を特定して報告**（D-2a/D-2bでは両方とも avg/modern は不変だった＝Fork隔離とno-opゲートが正しく機能している証拠）。
- 新プレー・新イベントは**既定オフ係数（RNG非消費）から**入れて段階確認。
- 1球判断のFork隔離（`rng.Fork(pitchStreamId)`）は統一後の directive にも適用。
- **【D-2bで踏んだ地雷・必読】**: `GameEngine.PlayHalfSteps` の1球采配クエリブロックで `PitchBattingOverride? battingOverride` に **PA単位の非null既定**（バントの `Bunt`/`SafetyBunt` など）を導入するたびに、それを上書きしうる**全ての代入箇所**が「値がある時だけ上書き」パターンになっているか確認すること。
  - 手動上書き（`if (offense.ConsumePendingPitchBattingOverride() is {} manualBatting) battingOverride = manualBatting;`）は元々このパターンで安全。
  - brain クエリ（`if (d?.Batting is {} brainBatting) battingOverride = brainBatting;` ※D-2bで修正済み）も同じパターンに揃えた。
  - **新しい非null既定を足す場合、この2箇所（brain・手動）のパターンを都度再確認すること**（D-2c/D-2dとも実際に確認済み・問題なし）。D-3でも同様に注意。`battingOverride`の既定が常にnullだった頃は無条件代入でも無害だったが、非null既定を足した瞬間に沈黙のバグになる（`AiGameWiringTests.SchoolStyle_ShapesTacticalActivity` が両校とも犠打0になって発覚した実例）。
- **digest再ベースラインの手順**（D-2bで確立・再利用可）:
  1. `engine/KokoSim.Engine.Tests/Match/Game/DeterminismBaselineDump.cs` の `[Fact(Skip = "...")]` から `Skip` を一時的に外す。
  2. `dotnet test engine/ --filter "FullyQualifiedName~DeterminismBaselineDump" --logger "console;verbosity=detailed"` を実行し、出力から `BL <card> <seed> <hash>` 行を抽出（`BL ` プレフィックスを取り除く）。
  3. `engine/KokoSim.Engine.Tests/Match/Game/determinism-baseline.txt` を新しい内容で置き換える（200行=4カード×50シード）。
  4. `Skip` を元に戻す。
  5. `dotnet test engine/ --filter "FullyQualifiedName~EngineDeterminismGateTests"` で緑を確認。
  6. 変化したカード/シード数を `diff` で確認し、想定通り（brain無し系は不変か）を確認してから報告。

## 5. コマンド

```bash
# dotnet が PATH に無ければ: export PATH="$PATH:/usr/local/share/dotnet"
# .claude/settings.json の permissions.allow に "Bash(dotnet *)" 済み＝承認不要
dotnet test engine/ --filter "Category!=Heavy"            # 日常（約1分20秒、632テスト）
dotnet test engine/ -c Release --filter "Category=Heavy"  # 統計回帰（Release必須、約1分20秒、20テスト）
dotnet run --project engine/KokoSim.Balance -- simulate --games 10000 --seed 42 --report out/report.md
bash tools/sync-engine-dll.sh                             # engine変更をUnityへ（UI確認前に必須）
```

## 6. Phase D の後

E 弾道判定（Trajectory→swing/contact接続・全帯再校正）。着手是非は D 完了後にユーザーが別途判断（Q12-1）。

## 7. 参照

- 設計: `docs/design/design-15-pitch-level-tactics.md`（§5 温存プレーの扱い・§6 Phase D行・§3 決定論）
- 投球内イベント: `docs/design/design-14-rule-completeness.md`（P2-8暴投/パスボール・乱数順注記・帯再校正の流儀）
- 走塁・読み合いの意味論: `docs/design/design-12-play-representation.md` §4（StartType/ピッチアウト/エンドラン=G1〜G3で実装済み、D-2dで崩さないこと）
- 采配仕様: `docs/design/design-09-in-game-tactics.md`
- Phase A/B/C 引き継ぎ（完了済み）: `HANDOFF-2026-07-19-pitch-level-phaseA/B/C.md`
- D-2bの新規テスト: `engine/KokoSim.Engine.Tests/Match/AtBat/BuntUnificationTests.cs`（count-carryover・BuntOutcome区別・GameEngineレベル配線確認のパターン集）
- D-2dの新規テスト: `engine/KokoSim.Engine.Tests/Match/AtBat/StealUnificationTests.cs`（毎球再判定の固定パターン集として、D-3のテストを書く時にも参照）
- D-2eの新規テスト: `engine/KokoSim.Engine.Tests/Match/Game/BattingOrderContinuationTests.cs`（打順の周期性・カウントリセット・save/replay一致の固定パターン集）
- D-3の新規テスト: `engine/KokoSim.Engine.Tests/Match/Game/BaserunningPlaysTests.cs`末尾（既定オフ/有効化パターンは他のP1/P2イベントと同型）
- 未決ログ: `docs/design/OPEN-QUESTIONS.md` Q12（クローズ済み）・Q13（クローズ済み、D-2eで方針(b)採用）・Q14（球のバウンド表現が無い件、D-3期間中に別途起票・Phase Dとは独立）

## 8. Phase D クローズ総括（2026-07-20）

### 8.1 A〜D で変わったこと（要約）

- **A**: `AtBatSession` を再開可能な投球ステッパへ（`GameStep.Pitch` 追加）。帯/digest 完全不変。
- **B**: `PitchRecord`（実弾道込み）を打席内に露出。`PitchSequenceSynthesizer` を置換。帯不変・digest 1回再ベースライン。
- **C**: `IPitchTacticsBrain` で AI/委任 brain を1球粒度化＋プレイヤー手動1球采配UI。**帯が動く**＝再校正、`tactics` 再ベースライン＋新カード `pitch-tactics` 追加。
- **D-1**: `MatchProgression.AdvancePitch()` で真の1球進行（セーブ/再現も球単位）。帯/digest 完全不変。
- **D-2a〜D-2d**: 敬遠・バント・スクイズ・盗塁/牽制/重盗を独立ミニループから統一ステッパへ順次統合。敬遠は digest 不変、バント/スクイズ/盗塁は`tactics`/`pitch-tactics`カードのみ想定通り再ベースライン（`avg`/`modern`＝無指示経路は全スライスを通じて不変）。Heavy帯は**一度も再校正不要**（既存の広め帯内に着地し続けた）。
- **D-2e**: 打順消費タイミングを是正（旧Q13）。副産物でバント/スクイズ決着時の二重`NextBatter()`バグも解消。
- **D-3**: 暴投・パスボールを新規実装（design-14 P2-8）。既定オフ係数のゲート実装で「計算後確率でなく生の基準係数を見る」を一度踏み外し、digest全崩壊で即発覚→修正（教訓は`pitch-level-tactics-design.md`に記録）。係数投入後もdigestは完全不変（digestカードは`data/coefficients.yaml`を読まないため）。
- **D-4**: 犠打成功率の集計を新設（副産物でdigest全カード再ベースライン、理由は文字列表現の変化のみと確認済み）。10000試合×2seedで全指標を実測し帯内を確認。Phase D クローズ。

### 8.2 実測ビフォーアフター（Balance CLI, `simulate-games --tactics`, 10000試合）

**得点/K/BB/HR**（Phase C 完了時点の記録値 vs Phase D 完了時点の実測。三振/四球/本塁打は `balance-targets.yaml` の `games_10k_tactics` コメントに残る Phase C-2 実測値と比較）:

| 指標 | Phase C 完了時点（記録値） | Phase D 完了時点（seed42 / seed2024） | 判定 |
|---|---|---|---|
| 三振率（打席あたり） | ≈19.1% | 19.12% / 19.06% | 帯内・実質不変 |
| 四球率（打席あたり） | ≈8.2% | 8.21% / 8.23% | 帯内・実質不変 |
| 本塁打率（打席あたり） | ≈2.85% | 2.84% / 2.84% | 帯内・実質不変 |
| 平均得点/チーム | ≈3.9-4.0（`games_10k_tactics`帯コメント） | 4.11 / 4.13 | 帯内（3.3–6.2） |

D-1〜D-2の統一作業は**構造のリファクタのみ**（`BuntResolver`/`SqueezeResolver`/`StealResolver`/`PickoffResolver` の確率式そのものは一切変更していない）で、各スライス後もHeavy帯（Category=Heavy）は一度も再校正が必要にならなかった。本リポジトリは git 管理外のため D-1着手前の実バイナリを再実行してのビフォーアフター採取はできないが、上表の通り Phase C 記録値と Phase D 最終実測値がほぼ完全一致していることが、構造リファクタが統計的性質を壊していないことの実測的な裏付けになっている。

**盗塁企図・成功率／犠打成功率**（Phase D で初めて Balance CLI レポートに載った指標。D-2d/D-2e 以前の同一集計値は記録が存在しないため、文字通りの「Phase C比較」はできない。以下は Phase D 完了時点の実測のみ）:

| 指標 | seed42 | seed2024 |
|---|---|---|
| 盗塁成功率（試行数） | 77.0%（582） | 72.3%（537） |
| 犠打成功率（試行数） | 78.1%（4302） | 79.0%（4203） |

盗塁企図数自体は `StandardTacticsBrain` の閾値依存で1試合あたり0.05〜0.06回程度と少なく試行数が限られるため、seed間で成功率が72〜77%とやや振れるが、両方とも常識的なレンジ（design-02の想定域）に収まっている。

**暴投・パスボール頻度**（D-3で新規実装。Phase C以前は機能自体が存在しない＝0/試合が文字通りの「before」）:

| 指標 | Before（機能無し / 係数0） | After（`data/coefficients.yaml`実係数） |
|---|---|---|
| 暴投・パスボール/試合（両軍計） | 0.000 | 0.558（seed42）/ 0.551（seed2024） |
| 平均得点/チーム（同一seed・同一yamlでWP/PB係数のみ0にした分離測定） | 4.09 | 4.11 |

得点への影響は+0.02/チームで、これはWP/PB追加による新規RNG消費が下流の乱数列をわずかに揺らすノイズの範囲内（他の指標: 三振19.12%→19.12%、四球8.18%→8.21%、本塁打2.85%→2.84%も同様の微差）であり、システマティックな帯シフトではない。

### 8.3 帯・digestの最終状態

- `data/balance-targets.yaml`: `games_10k.wild_pitch_per_game: {0.15, 1.00}` を新設。既存帯はすべて無変更。全帯が Phase D 完了時点の実測値を満たす（Heavy 20/20緑）。
- digestベースライン: `determinism-baseline.txt`（4カード×50シード=200行）は D-2b/D-2c(実質不変)/D-2d/D-2e/D-4 のタイミングで再ベースライン。D-1/D-2a/D-3 は無指示経路含め完全不変（再ベースライン不要）。`avg`/`modern` カード（brain無し経路）は Phase D 全体を通じて**一度も内容が動いていない**（TacticsTallyのToString変化を除く。これは文字列表現のみで挙動は不変）＝無指示経路の回帰保証は最後まで保たれた。
- テスト: `!Heavy 632` / `Heavy 20`、全緑。

### 8.4 設計書15／引き継ぎ書／メモリの更新

- `docs/design/design-15-pitch-level-tactics.md` §6 フェーズ計画の後に Phase D 完了の注記を追加。
- 本引き継ぎ書（`HANDOFF-2026-07-19-pitch-level-phaseD.md`）§0/§1/§2/§3/§8 を更新し、Phase D クローズを記録。
- 永続メモリ（`pitch-level-tactics-design.md`・`MEMORY.md`）を更新し、Phase D 完了・次は E の着手是非待ちである旨を記録。
- `docs/design/OPEN-QUESTIONS.md`: Q12/Q13 は既にクローズ済み・変更なし。Q14（球のバウンド表現）はPhase Dと独立のため未着手のまま。

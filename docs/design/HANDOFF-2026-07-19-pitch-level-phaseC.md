# 引き継ぎ — 設計書15 Phase C（1球采配＝AI 1球化＋手動采配ショートカット）2026-07-19

> 別チャットで **設計書15 Phase C** に着手するための単一引き継ぎ文書。
> 先に `docs/design/design-15-pitch-level-tactics.md` 全文（特に §2.3/§3.3/§0.1）を読むこと。

## 0. 前提（Phase A/B 完了済み・2026-07-19）

- **A**: `AtBatSession` ステッパ（`Match/AtBat/AtBatSession.cs`）・`GameStepKind.Pitch`・`ResolveDetailed`はSessionのdrainに一本化。
- **B**: 実1球データ `PitchRecord`（`Match/AtBat/PitchRecord.cs`・`PitchKind`の正定義もここ）を `PlayLogEntry.PitchLog` まで配線。Trajectory観測は `ctx.CaptureTimeline` ゲート（統計シムはゼロコスト10.6ms/game・観戦時42ms/game）。digestはPitchLog込みで再ベースライン済み（`determinism-baseline.txt` 150行・3ゲート緑）。`MatchProgression` は実データ優先・バント等null経路のみSynthesizerフォールバック。UI実機で実B/S点灯確認済み。
- 現状全緑: !Heavy 596 / Heavy 20・帯不変。**リポジトリはgit管理外**（コミット無し）。

## 1. 何をやるか（1行）

`IPitchTacticsBrain`（1球ごとの采配判断）を新設して Standard/Ai brain に実装（**AI 1球化**）、プレイヤーには**全球で采配ショートカット常駐**のUIを付ける。**このフェーズは帯が動く＝再校正イベント**。

## 2. これまでと違う点（最重要・2つ）

1. **帯が動く**: AIが1球ごとに読み合う＝結果が変わる。`balance-targets.yaml` の再校正と digestカード再ベースラインを**正面から**行う（A/Bの「不変」原則はこのフェーズでは「無指示経路のみ」に縮む）。
2. **チャット内でユーザー選択が2回必要**:
   - UI采配ショートカットの **ASCIIワイヤーフレーム3案提示→選択待ち**（UI-BUILD-METHOD.md / CLAUDE.md「実装の進め方」。勝手に選ばない）
   - AI 1球判断の**中身の設計**（どのカウント・状況で何を判断させるか）は design-09/11 に完全な仕様がない部分があるため、**閾値・判断項目の案を短く提示して合意を取ってから**実装（no-unilateral の精神はUIに限らない）

## 3. スコープ（Phase C でやる/やらない）

| 対象 | C でやる | 理由 |
|---|---|---|
| 打者への1球指示（強攻/待て/エンドラン/バスターの打撃補正） | **やる** | AtBatSession が既に扱える打撃系 directive |
| 守備の1球指示（配球方針 PitchPolicy/Gear の球単位切替） | **やる** | 同上（配球はループ内 PitchSelection が毎球参照） |
| 盗塁/バント切替/牽制/敬遠の**打席内**発動 | **やらない（Phase D）** | これらは従来経路のまま温存中（§5）。統一ステッパ化とセットでないとRNG順が壊れる |
| 伝令の打席内発動（GameStepKind.Timeout） | **やらない（Phase Dで判断）** | 伝令窓は現状打席頭のみ。統一時に一緒に検討 |

※スコープに疑義が出たら実装で勝手に広げず、OPEN-QUESTIONS Q12 派生として追記・報告。

## 4. 作業ブロック（この順で）

### C-1: `IPitchTacticsBrain` seam（engine・帯不変のまま）
- `IPitchTacticsBrain` 新設: `PitchDirective? CallPitchAction(in PitchTacticsSituation s, IRandomSource rng)`。
- `PitchTacticsSituation` = 既存 `TacticsSituation` ＋ `Balls/Strikes/PitchNumber/LastPitchOutcome(PitchKind?)`。
- `PitchDirective` = 打撃系（Swing強攻/Take/HitAndRun/Buster補正）＋守備系（PitchPolicy/Gear上書き）。**この球だけ**有効、次球は打席頭方針に復帰（Q12-3: 単純上書き）。
- GameEngine の投球窓で `brain is IPitchTacticsBrain pb` の時だけ呼ぶ。**非実装ならRNGを1発も引かない**（no-opゲート）。
- **Fork隔離**: 判断RNGは `rng.Fork(pitchStreamId)`（streamIdは inning/PA添字/球数から決定論的に導出）。主RNGを進めない。
- **このブロックの合否**: 誰も実装していない状態で全digest・全帯が**不変**（seamだけ入れて回帰緑を確認してから次へ）。

### C-2: Standard/Ai への実装（AI 1球化・**帯が動く**）
- `StandardTacticsBrain` に `IPitchTacticsBrain` 実装（委任采配の土台）。判断項目の案（例: 追い込まれたらTake禁止・3-0待て・エンドランのカウント条件・配球の球単位揺さぶり）を**先に短く提示して合意**。閾値は `TacticsCoefficients`（YAML）駆動＝バランス調整でC#を書き換えない（不変条件#4）。
- `AiTacticsBrain` は三層（校風→ティア→ミス）を1球判断にも被せる（design-11 の既存パターン踏襲）。
- **帯再校正**: Heavy実測→ `balance-targets.yaml` 着地→digestカード再ベースライン。`avg`/`modern` カード（brain無し）は**不変のまま**であること＝無指示経路の回帰証明。`tactics` カードは再ベースライン＋新カード `pitch-tactics` を追加。
- 実測値（得点/HR%/三振/四球等のビフォーアフター）を報告に残す。四球・三振率は1球判断の影響を最も受けるので特に注視。

### C-3: プレイヤー手動采配UI（unity・選択待ちあり）
- **先にASCIIワイヤーフレーム3案**（全球で采配ショートカット常駐・Q12-2確定）を提示して選択を待つ。UI原則⑦「操作は少なく深く」との密度緊張を捌く案を含めること（例: 常駐は小さく、展開はワンタップ）。
- 選択後: `MatchProgression` に手動 directive 注入口を追加（`_decisions` 記録で保存/再現互換）→ `MatchLiveController` に配線。DLL同期（`tools/sync-engine-dll.sh`）→バッチスクショ→7箇条自己レビュー。
- アクセント黄はCTA専用（[[rank-color-palette]]）。盤面ビューの色は状態コード（CLAUDE.md 例外規定）。

## 5. テスト（テストファースト）

1. **no-opゲート**: `IPitchTacticsBrain` 非実装brain／null directive で、全digestカード・帯が不変（C-1の合否）。
2. **Fork隔離**: `CallPitchAction` が何を返しても主RNG状態が判断前後で不変。
3. **上書き意味論**: 方針=強攻×1球=待て → その球だけ待て・次球は方針復帰。守備側も 方針=通常×1球=変化球中心 → その球だけ、で固定。（※旧記述「方針=バント×1球=強攻」は Phase C スコープ表と矛盾する例示ミス＝Q12-6でクローズ。バント切替の打席内発動は Phase D）
4. **委任再現**: 手動 directive が `_decisions` 経由で保存→復元後に同一結果（GameReplay 3ゲートの1球版）。
5. **帯**: C-2後の Heavy 全緑（再校正後の新帯で）。`avg`/`modern` digest不変・`tactics`/`pitch-tactics` 新ベースライン緑。

## 6. コマンド

```bash
# dotnet が PATH に無ければ: export PATH="$PATH:/usr/local/share/dotnet"
dotnet test engine/ --filter "Category!=Heavy"            # 日常（約10秒）
dotnet test engine/ -c Release --filter "Category=Heavy"  # 統計回帰（Release必須）
dotnet run --project engine/KokoSim.Balance -- simulate --games 10000 --seed 42 --report out/report.md  # 再校正の実測
bash tools/sync-engine-dll.sh                             # C-3前に必須（忘れるとCS0117）
```

## 7. Phase C の後（このチャットでは着手しない）

D統一（バント/スクイズ/盗塁/牽制/暴投をステッパへ・伝令窓の扱い・帯再校正） → E弾道判定（将来・全帯再校正・D完了後に別途判断）。詳細は設計書15 §6。

## 8. 参照

- 設計: `docs/design/design-15-pitch-level-tactics.md`（§2.3 IPitchTacticsBrain・§3.3 帯の動き方・§0.1 Q12決定）
- 采配仕様: `docs/design/design-09-in-game-tactics.md`（§1 サイン表・1球指示は方針より優先）
- 敵AI三層: `docs/design/design-11-enemy-ai.md`（ApplyStyle→TierGate→Misfire）
- UI手順: `docs/design/UI-BUILD-METHOD.md`＋CLAUDE.md UI原則7箇条（3案提示→選択待ちは必須）
- Phase A/B 引き継ぎ（完了済み）: `HANDOFF-2026-07-19-pitch-level-phaseA.md` / `-phaseB.md`

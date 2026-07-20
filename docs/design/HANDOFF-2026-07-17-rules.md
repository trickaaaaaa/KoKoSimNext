# 引き継ぎ文（2026-07-17）— ルール網羅・未実装プレー 作業ストリーム

別チャットへ移行する。**まずこの文書を読む** → 次に詳細設計 `design-14-rule-completeness.md`（この作業の正） → 全体像は `HANDOFF-2026-07-16.md`。
（別ストリーム「タイムライン拡充・F1」は `HANDOFF-2026-07-17.md`。混同しない。）

---

## 0. いま何をしているか（一言）

試合エンジンで**未実装・部分実装のプレーを洗い出し、実装計画を `design-14` にまとめ終えた**段階。
**第1段（P1）を一括実装→統計回帰・帯再校正まで完了**（2026-07-17）: 野選/振り逃げ/敬遠/牽制/失策連鎖は完了、重盗は一・三塁ケースのみ実装（単独三盗・本盗は resolver 層のみ、詳細は §3・`OPEN-QUESTIONS.md`）。
**全プレーの係数を実値に有効化し、`data/balance-targets.yaml`に新帯を追加して校正済み**（§3.5）。`dotnet test engine/ --filter "Category!=Heavy"` 473件、`-c Release --filter "Category=Heavy"` 19件とも全緑（2026-07-17・並行のトレーニング系セッション完了後に再確認。件数が477→473なのは同セッションのテスト整理によるもので本ストリームの変更によるものではない）。

**次にやること**: 第2段（P2）着手前にユーザーの合図を待つ。

**重要な設計判断（2026-07-17）**: 敬遠・重盗・牽制は「守備側/攻撃側の采配Brainが盗塁/敬遠を選ぶ」ことが発火条件だが、ヘッドレス統計シム（`GameSimulation`/Balance CLI）は元々**采配Brainを一切付けずにチームを生成**していた（K/9・BB/9等の既存帯も無指示前提で校正されたもの）。今回 `GameSimulation.Run` に `useTacticsBrain` オプション（CLIは`--tactics`）を追加し、両チームに`StandardTacticsBrain`（YAML係数駆動）を付与する専用経路を新設。既存の無指示シム・帯は**一切変更していない**（後方互換）。敬遠/重盗/牽制はこのBrainつき専用シム＋新設の`games_10k_tactics`帯でのみ検証する。

---

## 1. 作業環境・運用（必ず守る）

- **dotnet PATH**: `export PATH="$PATH:/usr/local/share/dotnet"` が必要（SDKは `/usr/local/share/dotnet`）。
- **日常テスト**: `dotnet test engine/ --filter "Category!=Heavy"`（約1分）。
- **統計回帰**: `dotnet test engine/ -c Release --filter "Category=Heavy"`（バランス係数・確率モデル変更時。**本ストリームはほぼ全プレーが該当**）。
- **git 管理外**（`Is a git repository: false`）。コミットしない。「1プレー=1コミットサイズ」は区切りの目安。
- **運用**: 1プレーごとに「設計書該当箇所を読む → 短い計画 → テストファースト → テスト緑 → 次はユーザーの合図を待つ」。勝手に次へ進まない。
- **不変条件**: 二層構造（物理層で解決・表示能力値を直接確率に使わない）／決定論（`IRandomSource` 注入・同シード同結果）／エンジン純度（`UnityEngine` 参照禁止）／データ駆動（係数は `data/*.yaml`）／統計帯維持（`data/balance-targets.yaml`）。コード=英語、コメント/ドキュメント=日本語。

---

## 2. これまでの成果（このストリーム）

### 調査（完了）
現行エンジンは「結果バケット型」（`PlateAppearanceResult`＝三振/四球/単〜本/凡打/失策 に落とし、`BaserunningModel` が合法遷移のみで進める）。
→ **ルール違反は原理的に起きない**。課題は「本来あるプレーが確率に吸収され個別表現されない／そもそも無い」＝**網羅性**。

### 設計の文章化（完了）
- **`docs/design/design-14-rule-completeness.md`** 新設＝本作業の正。§0前提／§1一覧（P1〜P3）／§2プレー別仕様／§2.5現代ルール／§3実装順序／§4テスト方針／§5ファイル見取り図／§6未決。
- ルート `CLAUDE.md` の設計書索引に design-14 を追記済み。

### ユーザー確定事項（重要・2026-07-17）
- **プレイは2026年スタート固定**。よって DH・タイブレーク・球数制限・**申告敬遠**・**コリジョンルール**は全て常時ON。過去年代分岐（`ModernRules.*IntroYear`）は基本ゲームで実装不要。
- **低反発金属バットは不採用**。ゲーム性優先で「普通の（よく飛ぶ）バット」を基準とし、**打球物理・帯は現行のまま**（追加作業なし）。design-14 §2.5-b 参照。

---

## 3. design-14 第1段（P1）実装ログ（2026-07-17・完了）

**段階計画**（詳細は design-14 §2〜§3）:

- **第1段（P1・出塁と采配）**: ✅野選(FC) / ✅振り逃げ / ✅敬遠(＋申告敬遠) / 🟡重盗（一・三塁のみ）・三盗・本盗（resolver層のみ） / ✅牽制 / ✅失策連鎖
- 第2段（P2・小技と細部）: 死球 / 暴投・パスボール / ライナー併殺 / 満塁本封 / コリジョン補正
- 第3段（P3・精度と演出）: タッグアップ二→三 / 併殺演出多様化(5-5-3等) / ランニングHR

全プレー共通の設計方針: **係数は既定オフ**（`FieldersChoiceProb`/`ErrorExtraAdvanceProb`/`DropThirdStrikeReachProb`/`IntentionalWalkProb`/`PickoffBaseProb`/`DoubleStealThirdBreakProb` いずれも `0.0`）。`MathUtil.Chance` は確率0でも `rng.NextDouble()` を1回消費するため、**分岐そのものをガードで丸ごとスキップ**して既定オフ時の乱数消費順・結果を従来と完全一致させるパターン（野選=FCで確立）を全プレーに踏襲。

### 野選（FC）P1-1
- `BaserunningCoefficients.FieldersChoiceProb`。`BaserunningModel.ApplyInPlayOut` の DP 判定 `if` の `else if` に追加。`ApplyInPlayOut`/`ApplyDetailed`/`Apply` の戻り値に `BatterSafeOnFc`（bool）を追加。新規 `BaserunningModel.IsBatterOut(result, batterSafeOnFc, droppedThirdStrikeReached)` を `GameEngine.cs` の `batterOut` 算出に接続（振り逃げと共用）。
- バグ修正: `GameEngine.cs` のタイムライン合成（併殺の6-4-3送球演出判定）が FC の二塁封殺 `RunnerMove` を誤って併殺と同一視していたため `&& !batterSafeOnFc` を追加。
- コア実装は並行セッションが先行、本セッションでレビュー・バグ修正・テスト一式（`BaserunningPlaysTests.cs`）を追加。

### 振り逃げ P1-2
- `BaserunningCoefficients.DropThirdStrikeReachProb`/`DropThirdStrikeCatchingSlope`。`GameEngine.cs` の `AtBatResolver.ResolveDetailed` 呼び出し後・`batterOut` 算出前に判定ブロックを追加（`bases.First is null || outs == 2` の条件下でのみ）。`PlateAppearanceResult` 列挙は変更せず`Strikeout`のまま（`IsAtBat()`/`IsHit()`で打数1・無安打が自動的に満たされる）。捕手`Catching`で成立確率が下がる式（`FieldingResolver.MaybeError`と同型）。
- テスト: `IsBatterOut`理論値＋`GameEngine.Play`統計テスト（弱打者チームで有効化時に得点増加を確認）。

### 失策連鎖 P1-6
- `BaserunningCoefficients.ErrorExtraAdvanceProb`。`BaserunningModel`の`case ReachedOnError`を`Single`から分離し、新規`ApplyErrorExtraAdvance`ヘルパーで「悪送球1本＝走者全員+打者が1つ多く進む」を一括モデル化（個別確率ではなく単一事象）。
- テスト: 既定オフでSingleと完全一致することを直接比較で確認、有効時の進塁パターン、平均得点上昇の統計テスト。

### 牽制 P1-5
- 新規 `PickoffResolver.cs`（`Match/Game/`）。`BaserunningCoefficients.PickoffBaseProb`/`PickoffRunnerLeadSlope`/`PickoffPitcherSenseSlope`/`PickoffMaxProb`。走者`Steal`が代理指標（リード幅）、投手`Mental`が代理指標（牽制の鋭さ、専用パラメータは選手モデルに無いため流用＝`OPEN-QUESTIONS.md`未決C）。`GameEngine.cs`の単独盗塁ブロック直前に挿入、ガードは`Resolve`内部（`PickoffBaseProb > 0.0`）。
- テスト: 確率0で常に不発、走者Steal/投手Mentalへの感応度、`GameEngine.Play`統計テスト。

### 重盗（一・三塁） P1-4（部分実装）
- `StealResolver`を`StealTarget`（Second/Third/Home）に一般化（末尾オプション引数、`target`省略時は完全後方互換）。三盗用`CatchThrowToThirdDistanceM`、三盗/本盗の下方バイアス`StealThirdSuccessBias`/`StealHomeSuccessBias`を追加。
- `GameEngine.cs`の単独二盗ブロック（`bases.First is not null && bases.Second is null`）内側は**無改修**。二盗の送球中に三塁走者も本塁を狙う「一・三塁重盗」だけを`DoubleStealThirdBreakProb`（既定0）で追加——このケースは元々「三塁走者を無視して単独二盗するだけ」の形で**既に到達可能**だったため、采配Brainの変更なしに実装できた。
- **単独三盗・単独本盗は未接続**（`StandardTacticsBrain.CallOffense`が現状「一塁走者・二塁空き」でしか`OffensiveSign.Steal`を返さないため、二塁のみ/三塁のみ在塁では実戦発生しない）。resolver層の物理式・単体テスト（`StealResolver_ThirdAndHome_AreHarderThanSecond`）のみ用意。詳細は`OPEN-QUESTIONS.md`「design-14 第1段（P1）実装の残課題」未決A参照。

### 敬遠・申告敬遠 P1-3
- `DefensiveTactics.IntentionalWalk`（bool）を追加。`TacticsCoefficients.IntentionalWalkMinPower`/`IntentionalWalkFromInning`/`IntentionalWalkMaxDiffAbs`/`IntentionalWalkProb`（既定0）。`StandardTacticsBrain.CallDefense`末尾で判定（一塁空き・得点圏・強打者・僅差終盤）。`AiTacticsBrain`は無改修で自動的にティア/ミス率ロジックを継承（`TierGateDefense`が`with`で他フィールドを保持するため）。
- `GameEngine.cs`で`defTactics.IntentionalWalk`が真なら`AtBatResolver`を一切呼ばず`BaserunningModel.Apply(..., PlateAppearanceResult.Walk, ...)`で直接確定、投球数0（申告制＝2026年ベースラインで常時）。
- テスト: `StandardTacticsBrain`単体（一塁走者ありでは発動しない等）、`GameEngine.Play`統合テスト（専用フェイクBrainで強制発動）。

**帯運用の鉄則**（不変条件#2/#5・遵守済み）:
- 各プレーは**係数の既定値で無効化した状態から入れ、既定オフでは従来帯と完全一致**を最初のテストで担保（設計書09/10 と同じ「無指示なら従来一致」方式）。
- P1 群をまとめて実装 → 一括で Heavy 回帰 → **帯の再校正も完了**（§3.5）。

## 3.5 統計回帰・帯再校正ログ（2026-07-17・完了）

design-14 §0.3の「その後に係数を有効化し帯を合わせる」を実施。[[deferred-stats-work]] は本件に関して解消。

### 発見: ヘッドレスシムは元々ノーBrain
`GameSimulation`（`KokoSim.Balance`、Balance CLI・`GameRegressionTests`が使う経路）は**采配Brainを一切付けずにチームを生成**していた。既存のK/9・BB/9等の帯もこの無指示前提で校正済み。
- 野選・振り逃げ・失策連鎖は`GameContext.Baserunning`駆動の「常時系」＝Brain不要でこのまま校正可能。
- 敬遠・重盗（一・三塁）・牽制は**采配Brainが盗塁/敬遠を選ぶことが発火条件**のため、無Brainのシムでは実測が常に0だった。

### 対応: シムにBrainオプションを追加（既存経路は非破壊）
`GameSimulation.Run`に`useTacticsBrain`（既定false）を追加。trueなら両チームに`StandardTacticsBrain(ctx.Tactics, ctx.Baserunning)`（YAML係数駆動、ステートレスなので並列ゲーム間で共有インスタンスOK）を付与。Balance CLIは`simulate-games --tactics`で有効化。**`useTacticsBrain`省略時（既存の全呼び出し）は挙動・帯とも完全に従来通り**。

### 校正値（`data/coefficients.yaml`, 2026-07-17時点。すべて🟡実測ベースの初期値、プレイテストで要調整）

| 係数 | 値 | 実測（両軍計/試合） |
|---|---|---|
| `baserunning.fielders_choice_prob` | 0.06 | 野選 ≈0.46–0.48 |
| `baserunning.error_extra_advance_prob` | 0.20 | 失策連鎖 ≈0.14 |
| `baserunning.drop_third_strike_reach_prob` | 0.001 | 振り逃げ ≈0.12（※下記注） |
| `baserunning.pickoff_base_prob` | 0.05 | 牽制アウト ≈0.001（Brainつきのみ） |
| `baserunning.double_steal_third_break_prob` | 0.4 | 一・三塁重盗 ≈0.002–0.003（Brainつきのみ） |
| `tactics.intentional_walk_min_power`/`from_inning`/`max_diff_abs` | 70 / 6 / 3 | 敬遠 ≈0.06（Brainつきのみ） |
| `tactics.intentional_walk_prob` | 0.6 | 同上 |

**注（振り逃げの非線形性）**: `DropThirdStrikeCatchingSlope`（捕手Catching依存の補正）が基準確率に対して相対的に大きいため、`Clamp(…, 0, 0.95)`の**下限クランプが非対称に効く**（下手な捕手＝+側は満額反映、上手な捕手＝−側は0で頭打ち）。結果、母集団平均の実測値が基準確率の単純な線形倍にならず、0.005→0.002→0.001と下げても実測が0.147→0.125→0.118としか下がらない「床」が生じた。**バグではなく捕手が下手なほど振り逃げが増えるという意図通りの非対称性の帰結**だが、基準確率の値だけを見て挙動を予測しないよう注意（触るときは必ず実測すること）。

### 新設した許容帯（`data/balance-targets.yaml`）
- `games_10k`に追加（Brain不要, 既存セクション）: `fielders_choice_per_game` / `dropped_third_strike_per_game` / `error_extra_advance_per_game`
- `games_10k_tactics`を新設（Brainつき専用）: `runs_per_team`（再チェック）/ `pickoff_per_game` / `intentional_walk_per_game` / `double_steal_third_break_per_game`
- `BalanceTargets.cs`に`GameTacticsTargets`レコード＋`BalanceTargetsLoader.LoadGameTacticsFromFile`を追加。

### テスト
`GameRegressionTests.cs`に`TacticsGames_StayWithinTargetBands`（Brainつき2000試合×2シード）を追加。既存`Games_StayWithinTargetBands`にも野選/振り逃げ/失策連鎖の帯チェックを追記。
`CoefficientsLoaderTests.LoadsRepositoryCoefficientsFile`のFC既定値ピン留めを`0.0`→`0.06`に更新（実値を固定して綴り違い等の配線ミスを検出する趣旨は維持）。

### 検証
`dotnet test engine/ --filter "Category!=Heavy"` 477件全緑。`dotnet test engine/ -c Release --filter "Category=Heavy"` 19件全緑（既存帯・新帯とも収束）。

**再確認（2026-07-17・並行トレーニング系セッション完了後）**: 直後に`data/coefficients.yaml`のコメント整形のみの編集を行った際、`CoefficientsFile.cs`が並行編集中の`TrainingCoefficients`/`FormCoefficients`のメンバ不整合でビルド不能になったが、これは本ストリームの変更が原因ではないため修正せず、並行セッションの完了を待った。完了後に再度`dotnet build engine/`→`dotnet test engine/ --filter "Category!=Heavy"`（473件全緑）→`-c Release --filter "Category=Heavy"`（19件全緑）を実行し、リポジトリ全体が正常であることを確認済み。件数が477→473に変わったのは並行セッション側のテスト整理によるもの。

---

## 4. 実装で触る主なファイル（design-14 §5 の要約）

| プレー | 主な変更ファイル |
|---|---|
| 野選 / 失策連鎖 / 振り逃げ / ライナー併殺 / 満塁本封 / タッグアップ | `Match/Game/BaserunningModel.cs`, `Match/Game/BaserunningCoefficients.cs`, `Match/Game/GameEngine.cs`（振り逃げは列挙追加なし＝`Strikeout`のまま`droppedThirdStrikeReached`フラグで表現） |
| 死球 / 暴投・パスボール（P2, 未着手） | `Match/AtBat/PlateAppearanceResult.cs`（列挙追加）, `Match/AtBat/AtBatResolver.cs`, `Match/Game/GameEngine.cs` |
| 敬遠・申告敬遠 | `Match/Tactics/TacticsTypes.cs`, `TacticsCoefficients.cs`, `StandardTacticsBrain.cs`, `GameEngine.cs`（`ITacticsBrain.cs`/`AiTacticsBrain.cs`は無改修） |
| 重盗（一・三塁のみ）／三盗・本盗（resolver層のみ） / 牽制 | `Match/Game/StealResolver.cs`（`StealTarget`一般化）, `Match/Game/PickoffResolver.cs`（新設）, `GameEngine.cs` |
| コリジョン補正（P2, 未着手） | `Match/Game/HomePlayResolver.cs` |
| 併殺演出の多様化（P3, 未着手） | `Match/Timeline/TimelineBuilder.cs`（判定不変・純演出） |
| 全プレーの係数 | `data/coefficients.yaml`, `KokoSim.Config/CoefficientsFile.cs`, `data/balance-targets.yaml`（`games_10k`拡張＋`games_10k_tactics`新設）, `KokoSim.Config/BalanceTargets.cs` |
| 統計回帰（Brainつきシム） | `KokoSim.Balance/GameSimulation.cs`（`useTacticsBrain`オプション）, `KokoSim.Balance/Program.cs`（`simulate-games --tactics`）, `Match/Game/TeamState.cs`/`GameEngine.cs`（新プレー発生数カウンタ, 統計参考値） |

---

## 5. 実装済み・対象外（再実装/再起票しない）

- **実装済み（P0）**: セーフティーバント（`OffensiveSign.SafetyBunt`＋`BuntResolver.InfieldHit`）、犠飛の三塁走者生還（`SacFlyScoreProb`＋G1深さ駆動）、本塁クロスプレー憤死（設計書12 F2）、スクイズのウエスト挟殺、DH/タイブレーク/球数制限（`ModernRules`・2026で自動ON）。
- **実装済み（P1・2026-07-17・すべて既定オフ）**: 野選(FC)、振り逃げ、失策連鎖、牽制、一・三塁重盗、敬遠・申告敬遠。詳細・触ったファイルは §3。
- **部分実装（再起票不要・OPEN-QUESTIONS参照）**: 単独三盗・単独本盗は`StealResolver`のresolver層のみ実装済み、采配Brain配線は未接続（`OPEN-QUESTIONS.md`未決A）。
- **対象外（構造上）**: インフィールドフライ（小フライは単なる凡打アウトで落球の安い併殺経路が無い＝IFFが必要な局面自体が無い）、ランダンプレー単体（各アウト確率に吸収）。
- **本ストリーム対象外（C・レア/演出）**: 打撃妨害・走塁妨害・ボーク・三重殺・隠し球など。

---

## 6. 未決事項（design-14 §6・OPEN-QUESTIONS.md・要ユーザー確認）

- 野選/振り逃げ/暴投等の成否を、当面の「捕手能力ベース低確率テーブル」から**二層（投球物理・捕球点）駆動**へ格上げする時期。
- 失策連鎖・暴投時の**失策/暴投/捕逸の個別記録**をどこまで持つか（当面は失点・進塁のみ、記録は簡略）。
- 本盗の発生条件（重盗・スクイズ文脈に限るか、単独本盗を許すか）。単独三盗・本盗を采配Brainへ接続する閾値設計（新規・`OPEN-QUESTIONS.md`未決A）。
- 敵AIの敬遠・重盗へのティアゲート/校風重み付け（新規・`OPEN-QUESTIONS.md`未決B）。
- 牽制の投手側パラメータ（`Player.Mental`を代理指標として流用中。専用パラメータを設けるか、新規・`OPEN-QUESTIONS.md`未決C）。
- コリジョンルールの本塁生還への補正量。

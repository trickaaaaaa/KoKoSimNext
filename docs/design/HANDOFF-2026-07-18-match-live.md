# 引き継ぎ: 試合描画 → 采配を挟む対話進行（2026-07-18）

別チャット移行用の単一引き継ぎ文。**まずこれを読む**。関連メモリ: `match-2d-playback` / `unity-batch-screenshot` /
`engine-dll-sync` / `dotnet-path` / `unity-execute-code-csharp6` / `ui-build-method-impl`。

---

## 0. このセッションで何をやったか（1行）

「試合が一瞬で終わる」→ **実試合を2D俯瞰で1試合まるごと観戦でき、さらに打席単位で采配を挟める対話進行**まで実装した
（設計者Claude の段階指示に沿って、モック忠実移植 → エンジン出力アダプタ → live配線 → **エンジンの打席単位ステップ化**）。

## 追記（2026-07-19）: 大会/タウンフローへの接続 完了（旧 §4-3）

**「監督の一戦を実試合として MatchLive でライブ観戦し、結果を大会へ戻す」導線を実装・検証した**（方式L＝ライブ駆動）。

- **制御反転**: 同期一括だった監督戦の解決を Begin/Complete に分割。`TournamentRunner.BeginNextPlayerMatch()` が
  当該ラウンドを自校カードの直前まで本流で解決して一時停止し `LivePlayerMatch`（進行体＋相手名＋ラウンド名）を返す。
  UI が打席単位で進め、終局の `GameResult` を `CompleteNextPlayerMatch(result)` へ戻して大会継続。
- **決定論の要**: 自校戦の詳細は隔離Fork（`PlayerMatchStream`）で進むため、本流RNGの消費順は自動
  `PlayNextPlayerMatch` とバイト一致。さらに `CaptureTimelines` はRNG中立（`collectMoves`/timeline構築は乱数不使用）。
  ⇒ **観戦してもしなくても大会結果は同一**。エンジン統合テスト `LiveBeginComplete_EqualsAutoPlayNextMatch`（3シード）で凍結。
- **新API**: `MatchProgression(Team,Team,GameContext,IRandomSource)` ctor（Fork直渡し。seedなし＝Save非対応）、
  `IPlayerMatchResolver.BeginLive`＋`PlayerMatchLive`、`TournamentRunner.BeginNextPlayerMatch/CompleteNextPlayerMatch`＋
  `LivePlayerMatch`、`GameSession.BeginMatch/CompleteMatch`、`HomeState.BeginMatch/CompleteMatch`、
  `MatchLiveController.Pending`（`LiveMatchRequest`）＋終了`OnComplete`＋「ホームへ戻る」ボタン、`ScreenRouter.ExtraScreens` に `MatchLive`。
- **導線**: 試合日「はい」→スタメン設定→OK→ホーム復帰(`AwaitingMatchStart`)→`StartLiveMatch()` が `BeginMatch()` で
  進行体を得て `MatchLiveController.Pending` を積み **`router.ShowDeferred("MatchLive")`**。終局→「ホームへ戻る」→`OnComplete` が
  `CompleteMatch(result)`（成績畳み込み＋`ResultPending`）→ホーム`OnEnable`が結果モーダル表示。ルータ不在時は従来 `PlayMatch()` 一括にフォールバック。
- **重要な落とし穴（修正済み）**: `StartLiveMatch` は `HomeDashboard.OnEnable`（＝スタメンOKの `router.Show("HomeDashboard")` の**内側**）で走る。
  ここで `router.Show("MatchLive")` を**同期**呼びすると Show がネスト（配信中に SetActive を切替）して**全画面が非アクティブ＝盤面が真っ黒**に落ちる
  （＝「試合開始でクラッシュ」の正体）。`ScreenRouter.ShowDeferred`（次フレーム Update で切替＝配信外）で回避。**OnEnable/Show の内側からの画面遷移は必ず ShowDeferred を使う**。
- **検証**: 非Heavy 579緑（新決定論テスト含む）＋決定論ゲート＋Heavy 20緑。Play実機で「準々決勝 vs 茅ヶ崎南高校」を
  ライブ観戦→7-0敗戦→敗退反映まで通し確認（`screenshots/match2d-live/tourney-1-live`,`tourney-2-final`）。使い捨てハーネスは削除済み。
- **残課題**: 中断保存（IRandomSource ctor は Save非対応。旧§4-5と統合して要設計）／代打以外の采配注入／采配UI本実装（旧§4-1・要相談）。

## 1. 現在の状態（全部 done・検証済み）

- **全テスト緑**: 非Heavy 569緑（+skip1）。Heavy `GameRegressionTests` 6緑（統計帯維持）。決定論ゲート緑。
- **不変条件を保持**: 二層構造・決定論・エンジン純度・データ駆動・統計回帰、すべて維持。
- エンジン変更後は **DLL同期済み**（`tools/sync-engine-dll.sh`）。
- Unity シーン（SampleScene）に **MatchDetail**（7サンプル再生ハーネス）と **MatchLive**（実試合・対話進行）の
  2 GameObject が保存済み（ともに既定 SetActive(false)・ScreenRouter 未登録の従属画面）。

## 2. 作ったもの（レイヤ別）

### エンジン（Unity非依存・dotnet testで検証）
| ファイル | 役割 |
|---|---|
| `Match/Timeline/Playback/PlaybackModel.cs` | mock-match-2d-view.html の再生モデル＋補間関数の忠実移植（pitch/arc/roll/throw/move/run） |
| `Match/Timeline/Playback/PlaybackSamples.cs` | mock の7プレー忠実データ（単一ソース） |
| `Match/Timeline/Playback/PlayTimelineAdapter.cs` | 実出力 `PlayTimeline`（端点From→To）→ 再生 `PlaybackPlay` 変換。EndpointBallSegment（種別ごと高さ） |
| `Match/Timeline/Playback/MatchPlaybackFeed.cs` | GameResult → 観戦プレー列（一括モード・リプレイ/スキップ用に残す） |
| `Match/Timeline/Playback/MatchProgression.cs` | **逐次供給ドライバ**（Advance=1打席／PinchHitUpcoming／SkipDelegateToAi） |
| `Match/Field/FieldDiagramGeometry.cs` | 球場「地理」の共通ソース（塁座標・Mound・FenceRadius式）。member図と試合図が参照（図法は別・地理は共通） |
| `Match/Game/GameEngine.cs` | **Play を Steps(GameProgress) イテレータ＋Play()=drain に改修**（単一コードパス）。NewProgress/Steps/BuildResult 公開 |
| `Match/Game/GameReplay.cs` | 中断保存（GameSaveState=シード＋確定打席数＋采配決定列・JSON可）／Restore=同シード再生で復元 |
| `Match/Game/TeamState.cs` | `OverrideTactics(brain)` 追加（スキップ委任で試合中に采配ブレイン差替。既定null=従来一致） |

新型: `GameProgress`（進行状態）、`GameStep`(+`GameStepKind`=PlateAppearance…**将来Pitch/Timeout予約**)、
`GameSaveState`/`GameDecision`、`LivePlateAppearance`。

### Unity
| ファイル | 役割 |
|---|---|
| `Assets/KokoSim/Match/Match2DPlaybackElement.cs` | Painter2D の2D俯瞰描画部品（drawField/drawToken/draw の忠実移植）。**両画面で再利用** |
| `Assets/UI/MatchDetail.uxml/.uss` ＋ `MatchDetailController.cs` | 7サンプル再生ハーネス（プレー選択・倍速・実況・結果チップ）。**触らない** |
| `Assets/UI/MatchLive.uxml/.uss` ＋ `MatchLiveController.cs` | **実試合の対話進行**（MatchProgression駆動・采配窓＝次へ/代打/スキップ・スコアボード） |
| `Assets/KokoSim/Match/MatchDetailBatchShooter.cs` / `MatchLiveBatchShooter.cs` | 使い捨てスクショ（#if UNITY_EDITOR）。α後に削除可 |

### テスト（決定論・受け入れ）
| ファイル | 保証 |
|---|---|
| `Match/Game/EngineDeterminismGateTests.cs` ＋ `determinism-baseline.txt` | **決定論ゲート**: avg/tactics/modern×50シードのGameResult完全一致SHA256を凍結。改修前後でバイト一致 |
| `Match/Game/GameResultDigest.cs` / `DeterminismCards.cs` | ダイジェスト＋代表カード。`DeterminismBaselineDump.cs`（Skip・再生成用） |
| `Match/Game/GameReplaySaveTests.cs` | 保存→JSON往復→復元→続行が中断なしと一致（打席途中含む） |
| `Match/Timeline/MatchProgressionTests.cs` | 手動全打席==batch／スキップ完走／7回代打が以降の打席に反映／代打決定が保存復元で再現 |
| `Match/Timeline/PlayTimelineAdapterTests.cs` / `MatchPlaybackFeedTests.cs` / `PlaybackGoldenTests.cs` | アダプタ・供給・モック一致のゴールデン |
| `Match/Timeline/CaptureTimelinesCostTests.cs` | CaptureTimelines コスト計測＋OFF保証 |

## 3. 設計の要（新チャットが壊してはいけない前提）

1. **決定論ゲートが最優先の安全網**。`GameEngine` に触ったら必ず `EngineDeterminismGateTests` を回す。
   1シードでも割れたら no-go＝原因特定まで進めない。帯を動かす改修は determinism-baseline.txt の**意図的再生成**が要る
   （`DeterminismBaselineDump` の Skip を外して実行→凍結）＋ Heavy 緑を確認。
2. **バッチと対話は単一コードパス**: `Play()` は `Steps(GameProgress)` を drain するだけ。ロジックは Steps 側だけにある。
3. **「采配なし==batch」を保つ**: 対話の采配注入は `TeamState.PinchHitNext` 直接呼び（**RNG非消費**）。
   ブレインを差すと tactics ブロックが RNG を消費して batch と分岐する（＝スキップ委任だけが `OverrideTactics` を使う）。
4. **裏試合はコストゼロ**: 全国4000校の裏試合は `AggregateMatch.PlayDetailed`（強さ抽象シム）で `GameEngine` を通らない。
   `CaptureTimelines=true` は**観戦する監督自身の一戦だけ**。既定OFFを保証テストで固定済み。
5. **図法は別・地理は共通**: member画面(非等方crop)と試合図(uniform全景)は投影が違ってよい。塁座標・FenceRadius式だけ
   `FieldDiagramGeometry` を両方が参照。
6. **MatchDetail(7サンプルハーネス)には手を入れない**。実試合は MatchLive 側。

## 4. 次にやること（未実装・優先度順）

1. **采配UI本実装（設計書09）**: サイン（バント/盗塁/エンドラン/待て…）・伝令（残回数ランプ3灯）・継投・守備陣形・
   配球方針。今は最小（次へ/代打/スキップ）だけ。`ITacticsBrain` 経路は既存だが、「采配なし==batch」を壊さないため
   **プレイヤー采配は RNG非消費の直接注入**か、**采配時のみブレイン経路**にするか設計判断が要る（要相談）。
2. **GameStep の投球単位細分化**: カウント間サイン・伝令タイミングのため `GameStepKind.Pitch` を実装
   （型は予約済み）。1球ごと yield は AtBatResolver の内部を開く必要があり大きめ。
3. **MatchLive を大会/タウンフローへ接続**: 現状は自前の固定シード試合を生成。本番は `PlayerMatchResolver` 経由の
   監督の一戦（`CaptureTimelines=true`）を MatchProgression に渡す。「試合開始→観戦画面」の導線を張る。
4. **代打以外の采配注入**（代走・守備固め・継投）を MatchProgression に追加（PinchHit と同じく直接注入＋決定記録）。
5. **中断保存の本配線**（GameSaveState をセーブデータへ／再開UI）。素地は GameReplay に完成。
6. **タイムラインなし打席（三振/四球）の演出**: 現状は静止＋結果テキストのみ。簡易カットインを足すか判断。
7. `MatchDetailBatchShooter` / `MatchLiveBatchShooter` の削除（α後）。

## 5. 作業環境メモ（ハマりどころ）

- **dotnet**: `export PATH="/usr/local/share/dotnet:$PATH"`。日常 `dotnet test engine/ --filter "Category!=Heavy"`（約1分）。
  Heavy は `-c Release --filter "Category=Heavy"`。
- **engine変更後は必ず `bash tools/sync-engine-dll.sh`**（忘れるとUnity側 CS0117）。
- **UnityMCP execute_code は codedom(C#6)**: `using` 不可（修飾名で書く）。Roslyn無し。拡張メソッド`Q`は
  `UnityElements.UQueryExtensions.Q<T>(root,name,null)`。
- **複数スクショは使い捨てバッチ MonoBehaviour コルーチンで**（Play 1回）。execute_code 連打はドメインリロードで
  **Play modeが抜ける**（`unity-batch-screenshot` メモリ参照）。スクショは Play中＋`Application.runInBackground=true`。
- **決定論ゲートは17秒／Heavy GameRegressionは18秒**。改修サイクルでこまめに回す。

## 6. 受け入れ証跡（このセッションで確認済み）

- 2D俯瞰: `screenshots/match2d/`（7プレー×2時点・モック忠実）。
- live観戦: `screenshots/match2d-live/live-1..5`（実試合の序盤/得点/最終・スコア/イニング/打者/結果が実データ整合）。
- 対話采配: `screenshots/match2d-live/live-accept-*`＋Console `[LiveAccept]` ログ
  （7回表に代打指示→7回裏で「代打 一郎」が打席→完走）。

# 引き継ぎ: MatchLive の UI 挙動修正（2026-07-19）

別チャットで **試合ライブ観戦画面 MatchLive の UI 挙動**を触るための単一引き継ぎ文。まずこれを読む。
背景の全体像は `HANDOFF-2026-07-18-match-live.md`（大会フロー接続＋クラッシュ修正の詳細）を、UI原則は
`CLAUDE.md`「UI原則7箇条」＋`docs/design/UI-BUILD-METHOD.md` を必ず併読。関連メモリ: `match-2d-playback` /
`unity-batch-screenshot` / `unity-execute-code-csharp6` / `panel-scale-crispness` / `ui-build-method-impl`。

---

## 0. これは何 / 何を触るか
MatchLive は「実試合を打席単位で 2D俯瞰再生しながら采配を挟む」対話進行画面。**このチャットの狙いは UI 挙動の修正**
（レイアウト・再生の見え方・采配窓の出方・スコアボード表示・ボタンの状態遷移など）。ロジック（エンジン/大会接続/決定論）は
完成・検証済みなので**基本触らない**。触るのは下記の Unity 側 UI ファイル群。

## 1. 触るファイル（UI 本体）
| ファイル | 役割 |
|---|---|
| `Assets/UI/MatchLive.uxml` | 画面構造。スコアボード(away-name/away-score/inning/home-score/home-name)、`field-host`(2D俯瞰の挿し込み先)、`batter`/`result-chip`/`caption`、操作(`next-pa`/`pinch-hit`/`skip`/`back-home`/`spd-1`/`spd-2`/`spd-4`) |
| `Assets/UI/MatchLive.uss` | この画面固有スタイル（`mlive-*` クラス）。共有は `tokens.uss`/`KokoSimTheme.uss`/`MatchDetail.uss` |
| `Assets/KokoSim/Match/MatchLiveController.cs` | 画面制御（namespace `KokoSim.Unity.Match`）。MatchProgression 駆動・再生ループ・采配窓・スコアボード更新・終局処理 |
| `Assets/KokoSim/Match/Match2DPlaybackElement.cs` | 2D俯瞰の描画部品（Painter2D）。MatchDetail と**共有**。座標系・フェンス弧・トークン描画はここ。**触るなら両画面に影響する点に注意** |

**触らない**: `MatchDetailController.cs`＋`MatchDetail.uxml/.uss`（7サンプル再生ハーネス＝別物）。エンジン(`engine/`)。

## 2. UI 挙動の要点（現状の設計）
- **モード2種**: ①ライブ(大会フロー)＝`MatchLiveController.Pending`(`LiveMatchRequest`)がセットされて OnEnable が消費 →
  実試合を観戦。終局で「ホームへ戻る(`back-home`)」を表示し `OnComplete(GameResult)` を呼ぶ。 ②デモ＝Pending なしで
  自前の固定シード試合を生成（スクショ/単体確認用）。`back-home` は既定 `display:none`、終局かつライブ時のみ表示。
- **1打席の流れ**: 采配窓(`EnterTacticsWindow`・再生停止/ボタン活性)→「次の打席へ」(`OnNextPa`→`ResolveAndReplayNext`)→
  タイムラインがあれば `_view.SetPlay` して `_replaying=true`、`Update()` が `_t += deltaTime*_speed` で `RenderReplayFrame`
  （`_view.SetTime`・caption・result-chip を `ResAt` で出す）→ 再生尺を超えたら采配窓へ戻る。タイムライン無し打席
  （三振/四球）は盤面静止＋結果テキストを `noPlayHoldSeconds` だけ見せて采配窓へ。
- **スコアボード**: 打前→`resolved`(=`_t>=ResAt`)で打後スコアへ切替。リード側は `mlive-score--lead`。
- **采配（最小実装）**: 次へ/代打(`pinch-hit`・自校の次打席へ)/スキップ(`skip`・残りを委任AI)。速度 `spd-1/2/4`(`chip-btn--on`)。
- **不変（UI変更で壊さない）**: 「采配なし==batch」の決定論。UI は表示だけ。`AdvanceForCapture`/`FreezeCurrentAtResult` 等の
  スクショ用フックは維持（バッチが使う）。

## 3. 実機で見る/直す手順（重要）
- **UI原則**: データ密度・整列・トークン(tokens.uss)内で。ただし**盤面ビュー(2D俯瞰)はアクセント本数制限の対象外**
  （色=状態コード。動き/走者=アンバー、警告=アラート。`mock-match-2d-view.html` の視覚言語を踏襲）。新規の見た目は
  部品辞書に足してから。迷う見た目の判断は実装せず質問。
- **スクショ**: 複数枚は**使い捨てバッチ MonoBehaviour のコルーチン**で（Play 1回・`Application.runInBackground=true`・
  panelSettings の `targetTexture` 差替でオフスクリーン撮影）。雛形は `MatchLiveBatchShooter.cs`(#if UNITY_EDITOR)。
  `execute_code` 連打はドメインリロードで Play が抜けるので不可（`unity-batch-screenshot` メモリ）。
- **決定論シーク不要**: 静止確認は `FreezeCurrentAtResult()`、1打席送りは `AdvanceForCapture()`（Update非依存）。
- **既存の受け入れ画像**: `screenshots/match2d-live/`（`live-*`＝デモ、`tourney-*`＝大会実試合）。

## 4. ハマりどころ
- **画面遷移は OnEnable/Show の内側からは `ScreenRouter.ShowDeferred` を使う**（同期 `Show` のネストで全画面非アクティブ＝
  盤面真っ黒。2026-07-19 に一度これで「試合開始クラッシュ」を出して修正済み）。
- **UnityMCP `execute_code` は codedom(C#6)**: `using` 不可(修飾名)、`required`/`init` メンバーの object initializer 不可。
  型は public メソッド/リフレクション経由で駆動（`unity-execute-code-csharp6`）。
- **パネルのスケール/滲み**: 整数 `scaledPixelsPerPoint` 前提。`ScreenRouter.EnforcePanelScale` が 1600x900/ScaleWithScreenSize
  に固定。撮影時は撮影解像度を参照解像度に合わせる（`panel-scale-crispness`）。
- **USS 落とし穴**: gap/grid 非対応、UXMLコメント内 `--` 禁止、コメント内 `*/` でファイル全体が空化、等
  （UI-BUILD-METHOD.md 末尾）。

## 5. 現状の健全性（このチャット開始時点）
- 全テスト緑（非Heavy＋決定論ゲート＋Heavy）。engine DLL 同期済み。Unity クリーンコンパイル。
- 大会フロー接続＋クラッシュ修正まで done。**UI 挙動の具体的な直したい点は、新チャットで実物を見ながら指示すること**
  （「どの挙動をどう変えたいか」を参照物・数値で。形容詞だけの指示は避ける＝UI-BUILD-METHOD の方針）。

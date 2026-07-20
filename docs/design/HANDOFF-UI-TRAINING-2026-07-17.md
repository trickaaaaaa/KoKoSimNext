# 引き継ぎ: 練習計画画面のUI作り直し（トークン＋部品辞書方式）

対象: **練習メニュー設定（練習計画）画面を1から作り直す**作業ストリーム専用の引き継ぎ。
別チャットはこの文を最初に読み、続いて `docs/design/UI-BUILD-METHOD.md`（手法の正典）を読むこと。

---

## ゴール

練習計画画面を、**トークン＋部品辞書方式（`docs/design/UI-BUILD-METHOD.md`）で1から作り直す**。
現行実装（下記）は「動くが見た目が未整形」で、**部品抽出の参考（初稿）**として使い、そのままは残さない。

## 決定事項（今夜確定）

1. UI構築の方法論として **トークン＋部品辞書方式** を採用（`UI-BUILD-METHOD.md` が正典）。
2. 練習計画画面はこの方式で **作り直す**。
3. **ViewModel（`TrainingPlanState.cs`）は良品。再利用する。** 作り直すのは表示層（UXML/USS/Controller配線）だけ。
4. HTMLモックは参照専用。機械移植しない。

## なぜ作り直すか（現行の問題）

現行 `Assets/UI/TrainingPlan.uxml` + `.uss` は機能的には動く（コンパイル0エラー・実行時例外0・全機能配線済み）が、
見た目が未整形。実機スクショで判明した崩れ:
- **ヘッダーと列の横ずれ**（右にいくほど累積）。原因＝ヘッダーとデータで列幅を別々にベタ書き。
- **「今週の見込み」列が全行カラ（—）**。1週ドライランでは能力がレベル閾値を跨がず加算0になるため。
  → 作り直しでは「レベルアップ」ではなく **exp進捗（次レベルまでの割合）** を見せる等、死に列にしない設計にする。
- **行の高さがバラつく**（配分が2段になる行で他セルが中央浮きして段差）。

これらは「インラインUSSを目視で微調整」が原因。トークンで列幅を一元化（ヘッダーとデータが同一 `--var` 参照）すれば構造的に消える。

---

## 現行資産（作り直しの起点）

| ファイル | 扱い |
|---|---|
| `Assets/KokoSim/Training/TrainingPlanState.cs` | **ViewModel。良品・再利用。** エンジン全データ→表示への変換を担う。エンジンDLL単体でコンパイル0エラー確認済み。 |
| `Assets/KokoSim/Training/TrainingPlanController.cs` | UI動的生成。**作り直し対象**（部品辞書から組む形に書き換え）。 |
| `Assets/UI/TrainingPlan.uxml` / `.uss` | **作り直し対象**。USSクラス（`.grade` `.trow` `.ubar` `.seg` `.mslot` `.switch` `.tp-camp` 等）は部品辞書の初稿＝抽出元。 |
| `Assets/UI/KokoSimTheme.uss` | 既存スコアボードテーマ＋ランク色。色は `tokens.uss` へ吸い出す。 |
| `Assets/KokoSim/Shell/ScreenRouter.cs` | 画面ルーター。ナビ項目に `name="nav-home/nav-players/nav-training/nav-tournament"` が必要。`Show("TrainingPlan")` で表示切替（GameObject SetActive・共有PanelSettingsのため常に1画面のみ）。 |

## 画面が表示すべきもの（仕様の要約・設計書06 §3.3 / 03 / 02§5.1 / 04§4）

- **選手ごと×週**の練習計画。バジェット **選手1人あたり週300分**、メニューへ分単位で配分。
- 構成: スコアボードヘッダー＋ナビ ／ 合宿バナー（夏×2・冬×2.5）／ 個別指導3枠 ／
  部員テーブル（背番号・選手・学年・守備・総合ランク・プリセット・強度3段・配分チップ＋使用率バー・見込み・週末疲労・複製）／
  選択選手エディタ（背番号・名前・学年・投打・総合・疲労・★指名・強度セグ・バジェットバー・委任トグル・メニュー±30分・守備適性9ポジ・週テンプレ保存）。
- **エンジン実データ駆動**: 背番号ランク・総合(Tiers.FromStrength)・配分/使用率・疲労・守備適性・合宿倍率・週送り。
- **UIセッション状態（エンジン未実装・視覚のみ）**: 個別指導3枠・委任・週テンプレ・学年フィルタ/ソート。

## エンジンAPI（調査済み・再調査不要）

- `FieldPosition`（enum, `KokoSim.Engine.Match.Field`, Pitcher..RightField の9）。JP表記ヘルパーはエンジンに無い。
- `Tiers.FromStrength(double 0-100) → Tier`（`KokoSim.Engine.Nation`）。`.ToString()` で "S".."G"。総合は `AverageLevel()` を渡す。
- `DevelopingPlayer`: `AverageLevel()` / `Aptitude(FieldPosition)` / `Level(AbilityKind)` / `Throws`,`Bats`（`Handedness` Right/Left/Switch）/ `Fatigue` / `Grade` / `IsPitcher` / `Name`。
- `SeasonCalendar`: `WinterCampWeek`=38 / `SummerCampWeek`=18 / `CampMultiplier(week, coef)` / `CanTrain(grade,week)` / `StageIndex` / `WeeksPerYear`=50。
- `TrainingMenu` enum に守備ポジション別 `DefenseP/C/1B/2B/3B/SS/LF/CF/RF`（各適性1.0）＋`DefenseInfield/Outfield`（0.5）。`TrainingPresets.Resolve(plan,isPitcher,budget)` / `TrainingMenus.Effects(menu)`。
- `TrainingCoefficients.DefaultBudgetMinutes`=300（選手1人/週）。

## Unity MCP の使い方（このマシン固有）

- MCP for Unity サーバー: `http://localhost:8080/mcp`（JSON-RPC+SSE）。**ツールレジストリ未接続**なので curl で叩く。
  手順: POST `initialize` → レスポンスヘッダ `Mcp-Session-Id` を取得 → `notifications/initialized` → `tools/call`。
  Acceptヘッダ必須: `application/json, text/event-stream`。
- 主要ツール: `refresh_unity`（再インポート）/ `read_console`（コンパイル・実行時エラー確認）/
  `execute_code`（`action:"execute"`必須・コードは値を `return` する）/ `manage_editor`（play/stop）/
  `manage_scene`（get_hierarchy）/ `execute_menu_item`。
- SampleScene に全画面GameObject＋ScreenRouter。`TrainingPlan` は既定で非アクティブ →
  `execute_code` で `FindObjectOfType<KokoSim.Unity.Shell.ScreenRouter>().Show("TrainingPlan")`。
- **スクショの教訓**: GameViewの手動キャプチャは空フレーム多発で不安定（今夜これで消耗）。
  GameViewが縦潰れした時は `execute_menu_item "Window/Layouts/Default"` で復帰した。
  **恒久対策＝UI-BUILD-METHOD Step3 の「RT+ReadPixels 決定論スクショ」を先に実装する**こと。手動撮り直しループに入らない。

## 権限（引き継がれる）

- `.claude/settings.local.json` に `defaultMode: "dontAsk"` ＋ curl/python3/tool.sh 等の広め許可を設定済み。
  ファイルなので別チャットにも引き継がれる。以降このワークフローでプロンプトは出ない想定。
- スクショ用ヘルパー（`/private/tmp/.../scratchpad/tool.sh`, `toolcode.sh`）はセッション固有で**引き継がれない**。
  別チャットで再生成するか、MCPを直接叩く。

---

## 次の手順（この順で）

1. `docs/design/UI-BUILD-METHOD.md` を読む（手法の正典）。
2. **`Assets/UI/tokens.uss`** を作る（色は `KokoSimTheme.uss` から吸出し＋**列幅・余白・タイポ**のトークン。列幅はヘッダー/データ共有）。
3. **スクショ自動化スクリプト**（RT+ReadPixels・batchmode可）を先に作る。以降の反復を速くする。
4. 現行 `TrainingPlan.uss` から**部品を抽出**（RankChip/NumberBadge/StatBar/SegmentedControl/Chip/Switch/StepperRow/Panel/Banner/Row）。
5. 練習計画画面を部品から**再構成**。`TrainingPlanState.cs`（ViewModel）はそのまま使う。「見込み」列は exp進捗表示に。
6. HTMLモック（`~/Downloads/高校野球 練習メニュー設定 (1).html`・自己完結バンドル。中身はpython+gzip展開で抽出可）と並べてスクショ比較、差分を箇条書き報告。

## 参考ファイル

- `docs/design/UI-BUILD-METHOD.md` — 手法の正典（最重要）
- `docs/design/design-06-ui.md` §3.3 — 練習計画の画面仕様
- `docs/design/design-03-*.md` §3 — 練習仕様（メニュー/強度/合宿位置づけ）
- `docs/design/design-02-*.md` §5.1 — 育成式（見込み表示の根拠）
- 未整形の現行実装: `Assets/UI/TrainingPlan.uxml` / `.uss`、`Assets/KokoSim/Training/*.cs`

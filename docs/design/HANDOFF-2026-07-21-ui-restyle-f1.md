# 引き継ぎ — 設計書16 F1（試作2画面・全面展開のGo/NoGo）2026-07-21

> 別チャットで **設計書16 F1** に着手するための単一引き継ぎ文書。
> 先に `docs/design/design-16-ui-restyle.md` を全部読むこと（§2 書体表と §6 フェーズ表は F0 で確定版に更新済み）。
> `docs/design/UI-BUILD-METHOD.md`（作り方の正典）と `CLAUDE.md`「UI原則7箇条」も**常に両方**に従う。
> 個別画面の仕様は `design-06-ui.md`。

---

## 0. 前提（F0 完了・2026-07-21）

### 0.1 書体は確定済み。**再議論しない**

検証ボードの実物をユーザーが見て決定した。design-16 §2 の表が正。

| 役割 | 書体 | USSクラス |
|---|---|---|
| ディスプレイ（太明朝） | Shippori Mincho B1 **Bold＋ExtraBold 併用** | `.f-display-lg`（Bold）／`.f-display`（ExtraBold） |
| 本文・UI | IBM Plex Sans JP Regular/Medium/SemiBold | `.f-body` / `.f-body-md` / `.f-body-bd` |
| データ数字 | Oswald Medium/SemiBold | `.f-num` / `.f-num-bd` |
| 掲示板 | DotGothic16 | `.f-dot` |

**罠1: `-lg` の方がウェイトは軽い。** `.f-display` = ExtraBold（16〜24pxの校名・見出し。暗背景で横画が残る方）、`.f-display-lg` = Bold（32px以上の大見出しだけ）。名前はウェイトでなくサイズ用途を表す。

**罠2: Oswald は欧文専用で和文グリフを持たない。** 数値だけを載せる Label に付ける。和文と混ぜる行は Label を分ける（既存の `sb2-cell__k` / `__v` 方式）。

`Assets/UI/tokens.uss` の末尾に上記クラスと、掲示板用の `--fs-dot-16/32/48`・`--sb-face: #0A0F0C` を定義済み。

### 0.2 書体クラスは**定義しただけで、まだどの画面にも当てていない**

現在の見た目は全画面ヒラギノのまま。差し替えは F1 の最初の作業（§2.1）。

### 0.3 フォント資産の状態

- TTF 8本＋OFLライセンス4本 → `Assets/UI/Fonts/`
- SDFアセット8本 → `Assets/UI/Fonts/Generated/`（1615字・未収録0・計205MB）
- 生成ツール → `Assets/Editor/FontAssetBuilder.cs`（メニュー「KokoSim/Fonts」）
- 生成条件: **64pt / padding 6 / SDFAA / 4096角 / AtlasPopulationMode.Static**
- 205MB のまま進めることはユーザー承認済み

### 0.4 ホーム画面は F0 の前提タスクとして情報構成を刷新済み（承認済み）

案A＝左に六角形レーダー＋故障者／中にチーム成績＋チーム内ランキング（打撃｜投手の横2列・1〜2位）／右にフィード＋個別指導。「次の試合」は大会モード時のみ表示。削除したのは「注目選手」「練習計画」。
**見た目は現行デザインのまま**なので、リスタイル適用は F3 で行う。最新スクショ = `screenshots/home-a-12-trim.png`。

### 0.5 リポジトリは未コミット

`git status` に F0＋ホーム改修の全変更が未コミットで乗っている（`Assets/UI/Fonts/` 245MB を含む）。**着手前にユーザーへコミット可否を確認すること。**

---

## 1. 何をやるか（1行）

**新書体と「二つの声のルール」を最小2画面（TopBar＋大会展望）に当てて、全面展開してよいかのGo/NoGoをユーザーに判断してもらう。**

---

## 2. 段階計画

### 2.1 F1-a: 既定フォントの差し替え（最初にやる・効果が一番大きい）

`Assets/UI/tokens.uss` の `.ui-root` が全画面の既定フォントを決めている。ここを旧 `KokoSimFont`（ヒラギノ）から `IBMPlexSansJP-Regular` へ差し替える。

```css
.ui-root { -unity-font-definition: url("...KokoSimFont.asset..."); }  /* ← これを Plex Regular へ */
```

**これは同時に既存バグの修正でもある。** 旧 KokoSimFont は `data/` が要求する1206字のうち **758字が欠落**している（基本ひらがなの「ぎ・ご・づ・ど・ぬ・ふ・ほ・ぼ・む・ゃ・ゅ・よ・ろ」を含む）。2026-07-21 のコミット `427783b` で Static 化した際、その時点で動的生成済みの667字だけが固定されたため。**今のビルドは校名・選手名の多くが豆腐（□）になっているはず。**

差し替え後は全画面のスクショを撮って豆腐が消えたことを確認する。旧フォントへの参照が残っていないか grep すること（F4のDoDだが、ここで大半が片付く）。

### 2.2 F1-b: TopBar の ScoreboardStrip 化

- `Assets/UI/Components/TopBar.uxml` の中央セル（現在／夏予選まで）を電光掲示板化する。黒面（`--sb-face`）＋ドット数字（`.f-dot`・16の整数倍のみ）。
- アンバーは点灯箇所（カウントダウン等の最重要値）のみ、基本は白ドット。
- **表示情報は現状と同一**（週・カウントダウン）。**スコアは出さない**（掲示板から借りるのは視覚言語であって情報ではない＝design-16 §1）。
- TopBar は全画面単一ソースなので1改修で全画面に波及する。

**着手時に定型どおりレイアウト3案をASCIIワイヤーで提示 → 選択を待つこと。**

### 2.3 F1-c: 大会展望を新文法へ

- 校名を太明朝（`.f-display`）に。SchoolName 部品として部品辞書に追加してから使う。
- 英語eyebrow（「TOURNAMENT PREVIEW ――」）を廃止 → 新聞の柱見出し風の日本語ラベル（縦棒＋明朝小見出し）。
- ピル型チップ → 角タイル（掲示板の枠）または罫囲み（新聞）。丸端バー → 角端。
- 数値列を `.f-num` へ。

### 2.4 F1-d: 文法カバレッジ確認

design-06 §3.6〜3.9 の未実装画面（スカウト・経営・キャリア・記録）を**新語彙で紙上レイアウト**し、部品辞書の穴を検出する（例: キャリア年表のタイムライン部品）。実装はしない。穴のリストを報告する。

### 2.5 F1 ゲート

before/after のスクショ比較を提出 → **全面展開の Go/NoGo をユーザーが判断**。

---

## 3. 着手時にユーザーへ聞くこと（design-16 §9・勝手に決めない）

1. **ScoreboardStrip の適用範囲**: 全画面ヘッダー（TopBar単一ソースなので自然）か、試合系画面のみか
2. **角丸の最終値**: 縮小方向で再検討する（掲示板・新聞の語彙はどちらも直線的）。現行はカード12px・モーダル16px

---

## 4. 作法（実地で確立済み・踏み外さない）

### 4.1 UnityMCP の使い方

- `.mcp.json` に `UnityMCP`（`http://127.0.0.1:8080/mcp`）が定義済み。**Unity Editor が起動していないとポートが開かない。**
- MCPサーバーはセッション開始時に接続される。セッション途中でブリッジを立てても**そのセッションのツール一覧には現れない**。その場合は curl で JSON-RPC を直接叩けば使える（`initialize` → `Mcp-Session-Id` ヘッダを取得 → `tools/call`）。
- `execute_code` は `action: "execute"` が必須。コードは **codedom（C#6相当）**でコンパイルされ、**必ず値を return** しないと「not all code paths return a value」で落ちる。`UnityEngine.UIElements` の `Q()` / `Query()` は拡張メソッドなので `UnityEngine.UIElements.UQueryExtensions.Q(root, "name")` と完全修飾で呼ぶ。

### 4.2 スクリーンショット

- **Play中**に `UnityEngine.ScreenCapture.CaptureScreenshot("screenshots/xxx.png")`。パスはプロジェクト直下からの相対。書き出しは非同期なので数秒待ってから読む。
- 再描画は `GameObject.Find("<画面名>").SetActive(false/true)` でコントローラの `OnEnable` を回すのが確実。
- USS だけの変更は Play 中でも `refresh_unity` → SetActive トグルで反映される。C# を変えたら**ドメインリロードで Play が抜ける**ので、stop → refresh → play し直す。
- 現在の Game View は 1600×900・`scaledPixelsPerPoint = 1`（整数＝滲まない条件を満たしている）。

### 4.3 レイアウトが崩れたら「見た目」でなく `resolvedStyle` を実測する

2026-07-21 に踏んだ罠: `height: 32px` を指定した行が、親カラムに入りきらないと **`flex-shrink` の既定値 1 で黙って 20px に縮んでいた**。エラーもログも出ず、見た目は「ちょうど収まっている」ように見える。同じ理由でカード自体も縮み、`overflow: hidden` で中身が切れていた（ホームの個別指導3枠目が消えていた）。

- `.card` に `flex-shrink: 0`、伸縮を担う `.card--grow` にだけ `flex-grow: 1; flex-shrink: 1; min-height: 0` という役割分担にしてある。**この分担を崩さない。**
- 固定高の行クラスには必ず `flex-shrink: 0` を付ける。
- 入りきらないと分かったら、縮ませるのではなく **ScrollView に入れるか、情報量を減らすか、横に展開する**。潰して収めるのは常に不正解。
- 詳細は `UI-BUILD-METHOD.md` の落とし穴節（この件を追記済み）。

### 4.4 語彙を増やしたらフォントを焼き直す

`data/` の校名語彙・選手名を増やしたら、必ずメニュー「KokoSim/Fonts/本番用の SDF を生成」を回す。`FontAssetBuilder.ProductionCharacterSet()` が `data/` 配下のYAMLを走査して必要文字を総取りする。**文字セットを手書きしない**（それが758字欠落の原因）。焼いた後は `characterTable` と必要文字を突き合わせて未収録0を確認する。

---

## 5. 守る資産（AI臭の原因ではない・変えない）

情報密度・行高（32px基準）・整列の規律、ランク色システム（S〜G色相ランプ＋文字併記）、日本語コピーの具体性、試合2D盤面の視覚言語。**触るのは声（書体）と手触り（面・形・語彙）であって骨格ではない。**

UI原則②の但し書きも忘れない: アンバーは1画面3箇所まで。ホームでは「今週を進める」＋カウントダウンで既に2箇所使うので、3つ目は慎重に。試合2D盤面は色が状態コードなので本数制限の対象外。

---

## 6. 参考スクショ

| ファイル | 内容 |
|---|---|
| `screenshots/home-a-12-trim.png` | 現行ホーム（情報構成 案A・旧フォント） |
| `screenshots/font-01.png`〜`font-04.png` | F0の書体検証ボード（明朝／本文／数字／掲示板ドット） |

検証ボード（`FontGallery.uxml/uss`）は不採用書体を参照していたため**削除済み**。書体を見比べたくなったら `FontAssetBuilder.BuildVerificationSet()` とボードを作り直すことになる。

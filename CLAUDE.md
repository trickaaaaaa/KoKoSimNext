# CLAUDE.md — KokoSimNext 開発ガイド

## プロジェクト概要

高校野球監督シミュレーションゲーム（フリーゲーム「高校野球シミュレーション4」の精神的後継・**独自タイトル**）。
プレイヤーは高校野球部の監督となり、育成・采配・キャリアを通じて甲子園制覇を目指す。
方針は「元祖に忠実＋現代化」。Unityで開発するが、**ゲームロジックはすべてUnity非依存の純C#エンジン**に置く。

## 分野別のルール（該当ファイルを読み書きしたときだけ自動で読み込まれる）

| ファイル | 対象 |
|---|---|
| `.claude/rules/ui.md` | UI原則7箇条・守備位置を出さない掟・書体・スコアブック・UI作業の定型（Assets の UI・USS/UXML・UI設計書） |
| `.claude/rules/3d-character.md` | 3Dキャラ制作の掟（対決/守備ビュー・Duel*・撮影/実測ハーネス・モーション・装備・シェーダ） |
| `.claude/rules/engine-tests.md` | engine テストの書き方（Heavy の置き場・xUnit の直列・並列化の作法） |

ルールを足すときは、全作業に効くものだけここ（CLAUDE.md）に書き、特定の領域にだけ効くものは `.claude/rules/` に `paths:` 付きで置く。

## 設計書索引（着手前に該当書の該当する章を読むこと。大きい設計書は `grep -n '^#'` で見出しを取り、要る章だけ読む）

| ファイル | 内容 |
|---|---|
| docs/design/design-01 | 二層パラメータ構造、選手モデル、打席解決パイプライン（6段階） |
| docs/design/design-02 | 能力スケールと物理層変換式、球種、精神力、走塁系解決式、育成式 |
| docs/design/design-03 | 週ターン制、年間カレンダー、練習、イベント3分類 |
| docs/design/design-04 | 監督メタ（分野別指導・転任・信頼/名声・資金）、スカウト/新入生、イベントYAML、合宿 |
| docs/design/design-05 | 大会構造、裏試合2層処理（全試合フルシム・記録解像度2層）、架空4000校生成、球場 |
| docs/design/design-06 | UI設計（UI Toolkit、スコアボードテーマ、画面一覧） |
| docs/design/design-07 | フェーズ計画とDoD（統計テスト） |
| docs/design/design-08 | 3Dアートディレクション（のっぺらぼう抽象方針） |
| docs/design/design-09 | 試合采配（サイン・守備指示・伝令・DH・タイブレーク・主将） |
| docs/design/design-10 | 特殊能力/スキル（有無フラグ制、data/skills.yaml） |
| docs/design/design-11 | 敵AI（三層＝能力値/ティア/校風、委任采配） |
| docs/design/design-12 | 詳細プレー表現（併殺・中継・走塁の読み合い）とタイムライン拡充 |
| docs/design/design-13 | 球場システム（フェンス距離/高さの物理層パラメータ化、data/stadiums.yaml） |
| docs/design/design-14 | ルール網羅・未実装プレー（野選/振り逃げ/敬遠/重盗/牽制/失策連鎖/死球/暴投等の実装計画と帯再校正） |
| docs/design/design-15 | 1球単位の試合進行と打席内采配（AtBatSessionステッパ化＋GameStep.Pitch＋IPitchTacticsBrain、4フェーズ移行、決定論/帯の担保） |
| docs/design/design-16 | UIリスタイル「テロップと電光掲示板」（書体3役＝太明朝/サンセリフ/コンデンス数字＋掲示板部品、ヒラギノ置換F0〜F4、フェーズゲート制）。本文・数字の書体は design-22 で Noto Sans JP に改訂 |
| docs/design/design-17 | デバッグモード（観測=PitchTrace/JSONL・再現=RNG状態＋再現トークン・注入=シナリオ/強制発動、MCP用DebugBridge、F0〜F4） |
| docs/design/design-18 | 選手キャラクター制作方針（対決ビュー用3Dキャラ＝リアル頭身のローポリ＋960×540・ポスタライズOFFのクリーン描画（ピクセル化ポストは温存・既定オフ。2026-08-05改定）、購入素体＋領域シェーダ、モーキャップ調達（購入パック＋Mixamo＋自作）、色替え7領域、時間量子化） |
| docs/design/design-19 | （データ画面は design-22 に置換・試合中の画面は存続）UIリスタイル「デーゲーム・フルNB」（昼の白地＋インク枠＋ベタ影＋芝/土/空の色ブロック＋応援団赤CTA。§8に実装確定事項＝夜トグル廃止/トップバーは電光掲示板島だけ黒/カードは.card一本化） |
| docs/design/design-20 | ドラフト（NPB指名・ドラフト候補・注目度）。注目度スコア（能力合成×隠し上限＋成績集計）／候補閾値・予想順位バンド／10月最終週の指名（3年のみ・独立Fork乱数で帯不変）／通知フィード連動・ドラフト画面IA |
| docs/design/design-21 | 表示能力→物理層の「効き」（`ability_effect:` 全員対称≈0.50・入口4つ＝ToBatter/ToFielder/Pitcher/反応球速＋変化量の実球速割引）と圧縮の撤去（raw＝中身）。球速は km/h を変えず反応オフセットで寝かせる／球速の価値（cq_per_kmh −0.005）／aggregate_scale 7.2／帯・テストの扱い |
| docs/design/design-22 | UIリスタイル「スコアブック」（紙色＋白カード＋明朝の名前＋Noto Sans JP。データ画面は `.sbk` スコープで切替・試合中の画面は対象外。書体と調子5色は全画面。主ボタンは応援団赤のまま。G0〜G4 フェーズゲート） |
| docs/design/design-23 | 性格の4軸×16タイプ・バッテリーの相性・チームの結束（issue #924・Q34〜Q40 決着済み）。4軸＝統率傾向/素直さ/勤勉さ/試合での姿勢、タイプは軸から導出するラベル。相性＝性格の出発点＋公式戦で組んだイニングの積み重ね。結束＝顔ぶれの土台＋合宿・不和の上下。効きは既存の解決式経由・小さく |
| docs/release/steam-roadmap | Steam 発売ロードマップ（段階0〜7・日付なし）。Windows のみ／体験版は2年目の夏まで・Steam DL版＋ブラウザ版／新タイトル＋実名の差し替え口／推奨スペック（仮）と計測の決め方 |

## 不変条件（違反するコードを書かない）

1. **二層構造**: 打席・打球・守備の解決は必ず物理層（初速・角度・回転・時間）で計算する。表示能力値を直接確率に使わない。表示層→物理層の変換式は一箇所（`data/coefficients.yaml` 駆動）に集約する。**表示能力の「効き」（`ability_effect:`・設計書21）もその一部**＝解決式が `Player` の能力を読むときは必ず `ToBatter(effect)/ToFielder(effect)/AbilityEffectCoefficients.Pitcher` か `c.AbilityEffect.Batting(...)` 等を通す（生の Lv を `(X−50)×slope` に入れない）
2. **決定論**: 乱数はすべてシード付き `IRandomSource` を注入して使う。`System.Random` の直接 new や `Guid` 由来の乱数は禁止。同シード同結果を常に保証する
3. **エンジン純度**: `engine/` 配下に `UnityEngine` への参照を持ち込まない。時刻・IO・乱数はすべて注入する
4. **データ駆動**: バランス係数・球種定義・イベント・校名語彙・球場は `data/` のYAMLに置く。**係数の正典は `data/` の YAML** だが、実ゲーム（Unity）と Balance CLI 既定は YAML を読まず **C#既定**（各 `new XxxCoefficients()`）で走る。したがって **C#既定は YAML と一致していなければならず、`CoefficientsDefaultsConsistencyTests` が全プロパティで一致を固定する（片方だけ変えない）**。バランス調整は YAML を編集し、**同じ値を C#既定にも反映する**（順序はどちらでもよいが、両方を揃えてコミットする）。一致テストが赤い＝実ゲームが校正前の係数で走っている合図（issue #402）。意図的な乖離（実ゲームでは意図的にオフ等）だけ、同テストの除外リスト（`ExcludedMembers`）に理由を添えて載せる（現在の除外は `Version`＝ファイル版数のみ。天候モデル `weather` は #803 で実ゲームに配線済みで除外していない）
5. **統計回帰**: バランスに影響する変更後は必ず統計回帰テストを実行し、`data/balance-targets.yaml` の許容帯を満たすこと

## リポジトリ構成

```
engine/KokoSim.Engine/        # ドメインロジック（Players / Match / Season / Nation / Career / Events）
engine/KokoSim.Engine.Tests/  # 単体テスト＋統計回帰テスト
engine/KokoSim.Balance/       # ヘッドレスシミュレーションCLI
data/                         # YAML一式
Assets/                       # Unity（UI Toolkit の画面・3D対決/守備ビュー）。engine は DLL で取り込む
tools/                        # 開発スクリプト（同期・検査・夜間エージェント・生成物の前処理）
docs/design/                  # 設計書01〜23・UI-BUILD-METHOD・OPEN-QUESTIONS
```

## コマンド

```bash
dotnet test engine/ -c Release --filter "Category!=Heavy"    # 日常ループ（約2分・2026-10-11実測・10コア）
dotnet test engine/ -c Release --filter "Category=Heavy"     # 統計回帰（約9分・2026-10-11実測・10コア。CIはシャード並列）
dotnet test engine/ -c Release                               # 全テスト実行
dotnet run --project engine/KokoSim.Balance -- \
  simulate --at-bats 10000 --seed 42 --report out/report.md   # 打席単位の一括シミュレーション＋統計レポート（--games は --at-bats の別名＝試合数ではない）
dotnet run --project engine/KokoSim.Balance -- \
  simulate-games --games 10000 --seed 42 --report out/games.md # フルゲーム単位（試合数）の統計レポート
dotnet run --project engine/KokoSim.Balance -- \
  ability-sweep --games 2000 --report out/ability-sweep.md  # 能力を1つずつ20/80に振り結果が動くか（約8分・issue #840）
```

- テストは必ず `-c Release` で回す（issue #361）。フルゲームシムは Debug だと約17倍遅く、日常ループが5分超になる。
  CI（fast/heavy）も `tools/sync-engine-dll.sh` も Release ビルド＝Debug で回す場面はない。
  engine に `#if DEBUG` / `Debug.Assert` は無いので検証内容も変わらない
- 統計回帰（`[Trait("Category","Heavy")]`）はバランス係数・確率モデルの変更時とコミット前に必ず回す（不変条件#5）
  要るかどうかは `bash tools/needs-heavy.sh`（終了コード0＝要る・理由を1行表示）で判定する。data/・試合結果の指紋
  （determinism-baseline.txt）・指紋で測っていない engine（Match/ 以外）・Heavy のテスト自体を触ったら要る。
  試合の解決（Match/）だけで指紋が不変なら帯は動かない＝省いてよい（日常ループの緑が前提）。CI は毎PR全 Heavy を回す
- **能力感度の検査**（`AbilitySensitivityTests`・Heavy）は、帯が測らない「能力を変えたとき結果が動くか／逆向きでないか」を見張る。
  期待は `data/balance-targets.yaml` の `ability_sensitivity`、既知の不具合は `known_defects` に issue 番号付きで載せる（直ったら外す＝外し忘れは赤になる）。
  新しく赤くなった能力は新しい穴。解決式・能力の効き（`ability_effect`）・采配AIを触ったら、`ability-sweep` の表で意図どおりか確かめる
- **`Assets/` の C# を触ったら `bash tools/check-unity-compile.sh`（Editor 不要・約10秒）を必ず回す**（issue #349）。
  engine の CI は `engine/ data/ global.json` にしか発火せず、**Assets だけを触る変更は engine テストが
  全部緑でも壊れたまま main に入る**（実例: PR #344 の CS0103 で Unity が12時間コンパイル不能／
  PR #390 の交代パネル二重表示）。`bash tools/install-git-hooks.sh` を一度実行すると pre-push で自動化される。
- **engine DLL（`Assets/Plugins/KokoSim/KokoSim.Engine.dll`）と `Assets/Resources/KokoSimData/` の YAML 複製は Git 管理外の生成物**（issue #785）。
  ゲームは YAML を `KokoSim.Unity.Shell.DataFiles`（Resources の TextAsset）からだけ読む＝ファイル IO で読まない（ブラウザ版で読めない・#1323）。
  並行 PR がマージのたびにバイナリ DLL で衝突していたため外した。**コミットするものは無い**（`git add -f` 禁止。Unity CI が検出する）。
  作るのは `bash tools/sync-engine-dll.sh`。clone 直後は `bash tools/install-git-hooks.sh` が一度回し、以降は
  checkout / pull / rebase の後にフックが engine/・data/ の変化を見て自動で回す。`check-unity-compile.sh` と Unity CI も検査前に作る。
  engine を編集中に Unity で確かめたいときだけ手で回す
- **`determinism-baseline.txt` が rebase で衝突したら手で直さない**。片側を採って rebase を終え、
  `bash tools/regen-determinism-baseline.sh` で作り直してコミットする（2本の PR が両方再生成すると必ず衝突する。issue #785）
  CI 側（`.github/workflows/unity-ci.yml`）は Unity ライセンスの Secret が入るまでスキップされる

## コーディング規約

- コード（型名・変数・コミット）は英語、コメント・ドキュメント・レポートは日本語
- ドメイン用語の対訳は下の用語集に従う。新しい対訳が必要になったらこの表に追記してから使う
- 1機能=1テスト以上。物理計算は既知の値との突き合わせテスト（例: 無回転球の到達時間）を必ず持つ
- 作業単位は小さく: 1タスク=1コミットサイズ。完了前に必ず `dotnet test` を通す

## 用語集（日本語 → コード名）

| 日本語 | コード名 | 備考 |
|---|---|---|
| ミート / パワー / 弾道 | Contact / Power / LaunchTendency | |
| 選球眼 / 走力 / 肩 | Discipline / Speed / ArmStrength | |
| 守備 / 捕球 | Fielding / Catching | Fieldingは反応・判断 |
| 球速 / コントロール / スタミナ | Velocity / Control / Stamina | |
| キレ（球種ランク） | PitchRank | 変化球の有効軸（Sharpness→rpm→変化量）。変化球練習(BreakingBall)の主効果。ストレートでは未使用 |
| 球威（ストレートホップ） | Stuff | ストレートの有効軸（Power→rpm→ホップ量）。球速練習(VelocityTraining)の副効果。変化球では未使用（Issue #274 で新設） |
| 球質タイプ | PitcherArchetype(Power/Finesse/SoftToss/Balanced/Flamethrower/Pinpoint/BreakingBallArtist) | 本格派/技巧派/軟投派/バランス型＋尖った型の剛腕/精密機械/変化球の魔術師（各3%・issue #908）。実戦の失点が型によらず揃うよう球速・制球・キレの配分だけ振り替える（`data/coefficients.yaml` の `roster: archetype_*`）。投手を比べるときは型の影響を外した地力の合計（`PitcherArchetypes.StrengthSum`）を使う |
| 投手総合 | PitcherComposite | 球速.40/制球.25/スタミナ.15/キレ.20（TeamStrengthCoefficients）。球速を最大要素にしつつ、他3つの合計(0.60)が球速(0.40)を上回る＝**球速単独では決まらない** |
| 投手役の導出 | PitcherRoleResolver.LeansPitcher | **選手は「投手である」属性を持たない**（design-01 §1.1a・#366）。投手適性>最良守備適性（＋供給バイアス）で毎回導出する。判定式はこのクラスにしか置かない |
| 投手寄せ重み / 投手らしさ | PitcherWeight(w) / RoleWeight(wRole) | w＝**バイアス無し**・能力の配分（打撃⇔投球）用。wRole＝**バイアス込み**で判定境界が0.5・持ち物（変化球本数・球質タイプ・投法）用。混ぜると全野手の打撃が下がり帯が動く |
| 性格の4軸 | Leadership / Receptiveness / Diligence / Selflessness | 統率傾向（既存）/素直さ/勤勉さ/試合での姿勢（献身⇔目立ちたがり）。1〜99・50が中立。性格タイプ（16）は軸から導出するラベル（設計書23 §1） |
| バッテリーの相性 | BatteryChemistry（出発点 S0／積み重ね B） | 投手×捕手。性格から決まる出発点＋公式戦で組んだイニングで育つ。効きは捕手リードの効き・動揺からの立ち直り・ピンチの制球（設計書23 §2） |
| 結束（画面の表示名は「チーム状態」＝言葉5段） | Cohesion（土台 U0／上下 ΔU） | 学校ごとの0〜100。画面には「チーム状態」としてトップバー等に言葉で出す（`ChemistryDisplay.TeamStateWord`・issue #1600）。部員の性格の顔ぶれ＋合宿・不和イベント。効きは精神面（動揺の起きにくさ・伝令の効き）だけ。チームの勢い（#863）とは別物（設計書23 §3） |
| 精神力 | Mental | 選手の疲労概念・練習強度は廃止（2026-07-17）。投手スタミナ（試合内球数消耗＝PitchingFatigue）は別概念で存続 |
| 才能上限 / 成長タイプ | PotentialCap / GrowthType(Early/Standard/Late) | 隠しパラメータ |
| 見極め | Insight | 隠しパラメータの推定精度 |
| 名声 / 信頼度 | Fame / Trust | 監督メタの二軸 |
| 部費 | Manager.Funds | **財布は1つ**（design-04 §1.4・issue #1082 で施設基金を統合）。学校予算・信頼度ぶん・学校の設備投資・後援会/OB寄付・甲子園/全国優勝の一時金が入り、施設・道具・備品・合宿・年契約・遠征すべてに使う。転任では3割だけ持ち越す |
| 買えるものの区分 | PurchaseKind(Facility/Equipment/Gear) | 施設（Lv1-3）／練習道具（効果小・大の2段）／備品（買い切り）。どれも部費から払う |
| 年契約 | ContractCatalog / ContractSet / ContractEffects | スカウト網・遠征・専門コーチ（design-04 §1.4a）。買い切りの施設と違い**効果が累積せず**「いまの段」だけが効き、年額を毎年部費から払い、段を0へ戻せば解約できる。既定はスカウト網Lv1＝従来の `ScoutCost` と同額・同効果 |
| 訪問の手応え | VisitResponse(Cold/Lukewarm/Warm/Eager) | スカウト候補を訪問した時点の成功率を4段に丸めた感触。入部の見込み（%）は出さず、これだけを返す（設計書04 §2.4） |
| 入部の条件（約束） | ScoutPromiseKind / ScoutPromiseStatus(Active/Kept/Broken) / ScoutPromises | 訪問のときたまに候補が出す条件（レギュラー確約・4番確約・連投させない等12種）。約束すると口説きやすくなり、入部後は本人が持つ（`DevelopingPlayer.Promise`）。公式戦で破ると信頼度と名声が下がる（設計書04 §2.4）。判定は `ScoutPromiseJudge`、采配AIに守らせる除外は `Team.PromiseGuard`（`MatchPromiseGuard`・自校の公式戦だけ・null＝従来どおり） |
| 練習分野 / 練習道具 | TrainingField(Batting/Defense/BaseRunning/Pitching) / EquipmentBonus | 施設の効率係数が全メニュー一律なのに対し、道具は**振っているメニューの分野にだけ**効く。メニュー→分野の写像は `TrainingFields` が単一ソース |
| 指導力（打撃/投手/守備走塁） | Coaching.Batting/.Pitching/.Defense | |
| 育成眼 / 采配 | TalentEye / TacticalSense | |
| 強さティア | Tier (G..S) | 学校属性 |
| プレッシャー指数 | PressureIndex | 設計書02 §3。PressureModel が算出 |
| 攻撃サイン | OffensiveSign | 強攻/待て/バント/セーフティバント/スクイズ/盗塁/エンドラン（設計書09 §1。バスターは作らない＝2026-11-23 オーナー判断） |
| 打撃姿勢 | BattingApproach | 待球策/転がせ/長打狙い/当てに行け。サインと違い**解除するまで続く構え**。効果は実効能力の倍率とスイング頻度の倍率だけ（設計書09 §1.1） |
| 守備指示 / 配球方針 | DefensiveTactics / PitchPolicy | 陣形は初期守備位置へ反映（設計書09 §2） |
| 申告敬遠 / ウエスト / バントシフト | IntentionalWalk / Pitchout / BuntShift | 守備のサイン2つ（敬遠＝その打席・ウエスト＝1球）と構え（設計書09 §1.2・issue #1473） |
| 采配の入口 | ITacticsBrain | プレイヤー・委任・敵AI共通（設計書09/11） |
| 伝令 | Timeout (Offense/Defense) | 攻守各3回・延長は回の頭で各1回に置き直す（積み増しではない。設計書09 §3） |
| 動揺 | Rattled | 連続出塁で発生、伝令・継投・イニング跨ぎで解除 |
| 主将 / 統率傾向 | Captain / Leadership | 統率力 = Leadership×Mental/100（設計書09 §8） |
| 指名打者 / タイブレーク | DhSlot＋StartingPitcher / TieBreakEnabled | 現代ルールトグル（設計書09 §6-7） |
| 選手交代 / 控え | Substitution(PinchHit/PinchRun/DefensiveSub/ReleaseDh) / Team.Bench | リエントリー禁止。TeamStateが可変ラインナップ保持（設計書09 §6, C-1） |
| 特殊能力 / 隠しスキル | Skill / SkillSet(Visible/Hidden) | 有無フラグ制（設計書10）。data/skills.yaml＋coefficients.yaml skills: |
| 敵AI / 三層プロファイル | AiTacticsBrain / AiProfile(TacticalSense/TierRank/Style) | 設計書11。ITacticsBrain実装で共通采配を流用 |
| 校風 | SchoolStyle | 機動力/強打待球/守り勝つ/全員/豪腕依存/型なし（設計書11 §3） |
| 大会フォーマット | PrefFormat / StageFormat(StageType) | knockout/round_robin/group_split（設計書05 §1.5, data/pref-formats/） |
| 地区大会枠 / センバツ選考 | RegionalFormat / SenbatsuSelection | 隔年・開催県+1・神宮枠（設計書05 §1.5/§4） |
| 現代ルール | ModernRules | DH/タイブレーク/球数制限の年代連動トグル（設計書05 §1.3） |
| 県内地区 | School.DistrictId | 地理固定割の添字（設計書05 §2.2） |
| 球場 / 球場格 | Stadium / StadiumTier(Municipal/Prefectural/National) | data/stadiums.yaml。寸法→FieldGeometry（設計書13） |
| 観客の入り | CrowdTurnout(Sparse/Half/Full) / CrowdTurnouts.For | 表示専用（issue #705）。大会と回戦から3段で決め、3Dスタンドの観客層 `crowd_0..2` の表示数になる |
| 両翼 / 中堅 / フェンス高 | LeftFenceM＋RightFenceM / CenterFenceM / FenceHeightM | 左右非対称対応。本塁打判定に直結（設計書13 §2） |
| 怪物 | Phenom | 才能外れ値の隠しフラグ。一芸特化型/総合型（OPEN-QUESTIONS Q20, design-04 §2） |
| 怪我の種類（傷病名） | InjuryType | 捻挫/肉離れ/骨折/打撲/靭帯損傷/疲労性炎症。定義は `data/injuries.yaml`（InjuryCatalog）。抽選は種類→部位→段階の順（design-03 §3.5） |
| 受傷の場面 | InjuryScene | 週次/死球/本塁クロスプレー/フェンス激突/スライディング/投球過多。試合中判定は Fork した専用ストリームで引き試合結果に影響させない |
| 1球トレース / 観測の出口 | PitchTrace / IDebugTraceSink | 設計書17。`GameContext.CaptureTrace`＋`TraceSink` の両方が揃った時だけ走る観測データ（既定オフ・帯不変） |
| 再現トークン | ReproToken | `k1:<rngState>:<pa>:<pitch>:<fixtureFp>`。指紋不一致は警告して再生しない（設計書17 §3.3） |
| 場面ジャンプ / 強制発動 | ScenarioStart / ForcedOutcome | デバッグ注入。`GameResult.ScenarioId` / `HasForcedOutcomes` が立った試合は digest・統計から除外（設計書17 §3.4/§6.1） |
| デバッグ橋渡し | DebugBridge | MCP `execute_code`（C#6）から叩く static＋JSON文字列のみのAPI（設計書17 §7） |
| 赴任イベント / チュートリアル | Tutorial（arrival_new_school / arrival_transfer）/ TutorialScene・TutorialProgress | マネージャーが出迎え・場面ごとに1回だけ進め方を教える。「はじめから」だけ・スキップ可・「もう説明はいらない」で以降出ない。進み具合はセーブ v42（design-04 §3.4・issue #1496） |

## 進め方

- 着手時: 該当設計書の該当章を読む → 実装計画を短く提示 → テストから書く
- 統計レポート（Balance CLI出力）は `out/` に置き、調整判断の根拠として会話に貼る
- 未決事項を発見したら実装で勝手に決めず、`docs/design/OPEN-QUESTIONS.md` に追記して報告する

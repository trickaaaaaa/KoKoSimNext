# CLAUDE.md — KokoSimNext 開発ガイド

## プロジェクト概要

高校野球監督シミュレーションゲーム（フリーゲーム「高校野球シミュレーション4」の精神的後継・**独自タイトル**）。
プレイヤーは高校野球部の監督となり、育成・采配・キャリアを通じて甲子園制覇を目指す。
方針は「元祖に忠実＋現代化」。Unityで開発するが、**ゲームロジックはすべてUnity非依存の純C#エンジン**に置く。

## 設計書索引（着手前に該当書を必ず読むこと）

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
| docs/design/design-16 | UIリスタイル「テロップと電光掲示板」（書体3役＝太明朝/サンセリフ/コンデンス数字＋掲示板部品、ヒラギノ置換F0〜F4、フェーズゲート制） |
| docs/design/design-17 | デバッグモード（観測=PitchTrace/JSONL・再現=RNG状態＋再現トークン・注入=シナリオ/強制発動、MCP用DebugBridge、F0〜F4） |
| docs/design/design-18 | 選手キャラクター制作方針（対決ビュー用3Dキャラ＝リアル頭身ローポリ＋ピクセル化シェーダ、モーキャップ調達、色替え6領域、時間量子化） |

## 不変条件（違反するコードを書かない）

1. **二層構造**: 打席・打球・守備の解決は必ず物理層（初速・角度・回転・時間）で計算する。表示能力値を直接確率に使わない。表示層→物理層の変換式は一箇所（`data/coefficients.yaml` 駆動）に集約する
2. **決定論**: 乱数はすべてシード付き `IRandomSource` を注入して使う。`System.Random` の直接 new や `Guid` 由来の乱数は禁止。同シード同結果を常に保証する
3. **エンジン純度**: `engine/` 配下に `UnityEngine` への参照を持ち込まない。時刻・IO・乱数はすべて注入する
4. **データ駆動**: バランス係数・球種定義・イベント・校名語彙・球場は `data/` のYAMLに置く。バランス調整でC#を書き換えない
5. **統計回帰**: バランスに影響する変更後は必ず統計回帰テストを実行し、`data/balance-targets.yaml` の許容帯を満たすこと

## UI原則（unity/ 配下のUI実装で常に従う）

> **UI実装の着手前に必ず `docs/design/UI-BUILD-METHOD.md`（作り方の正典）を読むこと。**
> この節（UI原則7箇条）は「何を目指すか＝判断基準」、UI-BUILD-METHOD.md は「どう作るか＝手順」。
> 2つは補完関係で、UI作業では**常に両方**に従う。個別画面の仕様は設計書06および各機能設計書を参照。

このゲームのUIは **Football Manager型のデータ濃密UI** である。今風の余白たっぷりミニマルUIに寄せない。
迷ったら以下の7箇条とスクショ自己レビューで判断する。

### 7箇条

1. **密度は正義、ただし整列で捌く**: 1画面の情報量は多くてよい。読みやすさは余白を増やすのではなく、
   グリッド整列・列の揃え・一貫した行高で作る。データ画面の行高は詰める（32px基準）
2. **強調は希少資源**: アクセント（アンバー #F5C64A）は1画面3箇所まで。全部を目立たせる＝何も目立たない。
   警告色（#E86A4A）は本当の警告（怪我・疲労・敗退リスク）だけに使う
3. **数字が主役**: 数値はコンデンス書体・右揃え・桁を揃える。ランクは必ずカラーチップ＋文字併記（色覚対応）。
   数字を装飾で飾らない（グロー・影・グラデ禁止）
4. **区切りは余白と背景色で、線は最小限**: 装飾のためのボーダーを引かない。パネル背景（#1D3227）と
   背景（#14231B）のコントラストで領域を分ける。線を引くのは表のヘッダ下など機能がある場所だけ
5. **トークンと部品辞書の外に出ない**: 色・サイズ・余白は tokens.uss の変数のみ。見た目の新規要素は
   部品辞書（RankChip / StatBar3 / ConditionFace 等）に追加してから使う。画面内での直書き・独自実装は禁止
6. **状態は一目で**: 調子は表情顔、ランクは色、疲労・怪我は警告色ドット。テキストを読まなくても
   一覧をスキャンするだけで状態が拾えること（眺めてニヤニヤできる画面が正義）
7. **操作は少なく深く**: 主要アクション（今週を進める等）は1画面1つ、大きく。それ以外の操作は
   コンテキスト（行クリック→詳細）に寄せ、ボタンを並べない

> **適用範囲の例外（試合盤面）**: ②の「アクセントは1画面3箇所まで」の本数制限は**データ画面**に適用する。
> 試合2D俯瞰などの**盤面ビューでは色は状態コード**（動き＝アンバー／走者＝アンバー／警告＝アラート）であり、
> 本数制限の対象外（mock-match-2d-view.html の視覚言語を踏襲）。将来、動き色と走者色の区別が必要になったら相談する。

> **書体3役ルール（設計書16。詳細は UI-BUILD-METHOD.md）**: 文字は役割で書体を割り当てる。
> **明朝**（`.f-display*`）＝校名・大会名・見出し・**人名**／**サンセリフ**（`.f-body*`）＝本文・ラベル・ボタン／
> **Oswald**（`.f-num*`）＝**数字だけのセル**／**ドット**（`.f-dot`）＝掲示板部品の中だけ。
> Oswald は欧文専用なので**数字＋和文の混植は Label を分ける**（`UiComponents.NumUnit`）。疑似ボールド
> （`-unity-font-style: bold`）は禁止＝実ウェイトクラスで太字を出す。

### 実装の進め方（UI作業の定型）

- **実装前**: 新しい画面はいきなり組まず、レイアウト案をASCIIワイヤーフレームで3案提示 → 選択を待つ
- **実装中**: HTMLモック（mocks/）は参照専用。機械移植しない。部品辞書の組み合わせで構成する
- **実装後**: バッチスクショ（UIScreenshot.Capture）を出力し、上の7箇条に照らして自己レビュー
  → 違反を修正してから提出。人間の目視は最終確認のみ
- 参照物と数値で指示された場合（「FM風の密度」「行高32px」）はそれを最優先。形容詞（いい感じ・モダン）で
  自己判断しない。判断に迷う見た目の選択は実装せずに質問する

## 3Dキャラ制作の掟（設計書18。対決ビュー用3Dキャラの作業で常に従う）

- URPは品質設定（Quality）ごとに別RenderPipelineAssetを持つ。Render Feature追加・シェーダ設定は
  Graphics / Quality 両方のRenderPipelineAsset整合を確認してから行う（design-18 §2）
- AnimatorのMirror ON（左打者・左投手）時は道具のアタッチ先ボーンも左右反転が必要。
  切り替え処理は専用コンポーネント1つに集約する（design-18 §3.4）
- 購入モーションFBX（AA Basic Baseball等）を公開リポジトリに含めない（再配布NG。design-18 §3.1）
- チームカラー→マテリアル流し込みは tokens.uss と同型のパターンとして部品辞書に登録する（design-18 §3.5）
- 素体は6領域（ユニ上/ズボン/アンダーシャツ/帽子クラウン/つば/ソックス）でUV区分けする。
  区分けはbpyスクリプトで行い、手作業でUVを触らない（design-18 §3.5.2）
- 縦縞はテクスチャに焼かず、シェーダプロパティ（ON/OFF・縞色・周波数）で手続き生成する（design-18 §3.5.3）
- キャラのポーズ更新は時間量子化（12fps相当・専用コンポーネントで制御）。**ボール・カメラはフルレート維持**。
  量子化対象を混同しない（design-18 §4.5-②）
- モーキャップクリップの内部緩急を改変しない。同期は再生開始時刻のアンカー逆算（AnimationEvent基準）で行い、
  捕球・ミートの端数合わせはEvent直前の短時間IKで吸収する（design-18 §4.5 / §7-2）

## リポジトリ構成

```
engine/KokoSim.Engine/        # ドメインロジック（Players / Match / Season / Nation / Career / Events）
engine/KokoSim.Engine.Tests/  # 単体テスト＋統計回帰テスト
engine/KokoSim.Balance/       # ヘッドレスシミュレーションCLI
data/                         # YAML一式
unity/                        # UIと将来の3D観戦（Phase 6まで触らない）
docs/design/                  # 設計書01〜07
```

## コマンド

```bash
dotnet test engine/ --filter "Category!=Heavy"        # 日常ループ（約10秒）
dotnet test engine/ -c Release --filter "Category=Heavy"   # 統計回帰（約15秒。要 Release ビルド）
dotnet test engine/                                   # 全テスト実行（Debugだと統計回帰が数分）
dotnet run --project engine/KokoSim.Balance -- \
  simulate --games 10000 --seed 42 --report out/report.md   # 一括シミュレーション＋統計レポート
```

- 統計回帰（`[Trait("Category","Heavy")]`）はバランス係数・確率モデルの変更時とコミット前に必ず回す（不変条件#5）
- 統計シム（GameSimulation/AtBatSimulation）は決定論保存で並列化済み（事前Fork＋整数部分和）。同シード同結果は維持される

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
| キレ（球種ランク） | PitchRank | 内部は SpinRate＋SpinEfficiency の2軸 |
| 球質タイプ | PitcherArchetype(Power/Finesse/SoftToss/Balanced) | 本格派/技巧派/軟投派/バランス型。投手総合を変えず球速・制球・キレの配分だけ振り替える（`data/coefficients.yaml` の `roster: archetype_*`） |
| 投手総合 | PitcherComposite | 球速.40/制球.25/スタミナ.15/キレ.20（TeamStrengthCoefficients）。球速を最大要素にしつつ、他3つの合計(0.60)が球速(0.40)を上回る＝**球速単独では決まらない** |
| 精神力 | Mental | 選手の疲労概念・練習強度は廃止（2026-07-17）。投手スタミナ（試合内球数消耗＝PitchingFatigue）は別概念で存続 |
| 才能上限 / 成長タイプ | PotentialCap / GrowthType(Early/Standard/Late) | 隠しパラメータ |
| 見極め | Insight | 隠しパラメータの推定精度 |
| 名声 / 信頼度 | Fame / Trust | 監督メタの二軸 |
| 指導力（打撃/投手/守備走塁） | Coaching.Batting/.Pitching/.Defense | |
| 育成眼 / 采配 | TalentEye / TacticalSense | |
| 強さティア | Tier (G..S) | 学校属性 |
| プレッシャー指数 | PressureIndex | 設計書02 §3。PressureModel が算出 |
| 攻撃サイン | OffensiveSign | 強攻/待て/バント/スクイズ/盗塁/エンドラン/バスター（設計書09 §1） |
| 守備指示 / 配球方針 | DefensiveTactics / PitchPolicy | 陣形は初期守備位置へ反映（設計書09 §2） |
| 采配の入口 | ITacticsBrain | プレイヤー・委任・敵AI共通（設計書09/11） |
| 伝令 | Timeout (Offense/Defense) | 攻守各3回・延長は毎回+1（設計書09 §3） |
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
| 両翼 / 中堅 / フェンス高 | LeftFenceM＋RightFenceM / CenterFenceM / FenceHeightM | 左右非対称対応。本塁打判定に直結（設計書13 §2） |
| 怪物 | Phenom | 才能外れ値の隠しフラグ。一芸特化型/総合型（OPEN-QUESTIONS Q20, design-04 §2） |
| 怪我の種類（傷病名） | InjuryType | 捻挫/肉離れ/骨折/打撲/靭帯損傷/疲労性炎症。定義は `data/injuries.yaml`（InjuryCatalog）。抽選は種類→部位→段階の順（design-03 §3.5） |
| 受傷の場面 | InjuryScene | 週次/死球/本塁クロスプレー/フェンス激突/スライディング/投球過多。試合中判定は Fork した専用ストリームで引き試合結果に影響させない |

## 進め方

- 現在フェーズ: **Phase 0 → 1**（design-07 §3 参照）
- フェーズ開始時: 該当設計書を読む → 実装計画を短く提示 → テストから書く
- 統計レポート（Balance CLI出力）は `out/` に置き、調整判断の根拠として会話に貼る
- 未決事項を発見したら実装で勝手に決めず、`docs/design/OPEN-QUESTIONS.md` に追記して報告する

## 最初のタスク（Phase 1 着手点）

「ストレートの弾道積分（重力＋空気抵抗＋マグヌス力）」と「コントロールσによる投球散布」を、
テストファーストで `KokoSim.Engine.Match.Pitching` に実装する。
検証: 145km/h・回転数2200rpmのストレートの到達時間とホップ量が物理的に妥当な範囲に入ること。

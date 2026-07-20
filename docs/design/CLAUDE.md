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
| docs/design/design-05 | 大会構造、裏試合3層処理、架空4000校生成、球場 |
| docs/design/design-06 | UI設計（UI Toolkit、スコアボードテーマ、画面一覧） |
| docs/design/design-07 | フェーズ計画とDoD（統計テスト） |

## 不変条件（違反するコードを書かない）

1. **二層構造**: 打席・打球・守備の解決は必ず物理層（初速・角度・回転・時間）で計算する。表示能力値を直接確率に使わない。表示層→物理層の変換式は一箇所（`data/coefficients.yaml` 駆動）に集約する
2. **決定論**: 乱数はすべてシード付き `IRandomSource` を注入して使う。`System.Random` の直接 new や `Guid` 由来の乱数は禁止。同シード同結果を常に保証する
3. **エンジン純度**: `engine/` 配下に `UnityEngine` への参照を持ち込まない。時刻・IO・乱数はすべて注入する
4. **データ駆動**: バランス係数・球種定義・イベント・校名語彙・球場は `data/` のYAMLに置く。バランス調整でC#を書き換えない
5. **統計回帰**: バランスに影響する変更後は必ず統計回帰テストを実行し、`data/balance-targets.yaml` の許容帯を満たすこと

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
dotnet test engine/                                   # 全テスト実行
dotnet run --project engine/KokoSim.Balance -- \
  simulate --games 10000 --seed 42 --report out/report.md   # 一括シミュレーション＋統計レポート
```

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
| 精神力 | Mental | 選手の疲労概念・練習強度は廃止（2026-07-17）。投手スタミナ（PitchingFatigue）は別概念で存続 |
| 才能上限 / 成長タイプ | PotentialCap / GrowthType(Early/Standard/Late) | 隠しパラメータ |
| 見極め | Insight | 隠しパラメータの推定精度 |
| 名声 / 信頼度 | Fame / Trust | 監督メタの二軸 |
| 指導力（打撃/投手/守備走塁） | Coaching.Batting/.Pitching/.Defense | |
| 育成眼 / 采配 | TalentEye / TacticalSense | |
| 強さティア | Tier (G..S) | 学校属性 |
| プレッシャー指数 | PressureIndex | 設計書02 §3 |

## 進め方

- 現在フェーズ: **Phase 0 → 1**（design-07 §3 参照）
- フェーズ開始時: 該当設計書を読む → 実装計画を短く提示 → テストから書く
- 統計レポート（Balance CLI出力）は `out/` に置き、調整判断の根拠として会話に貼る
- 未決事項を発見したら実装で勝手に決めず、`docs/design/OPEN-QUESTIONS.md` に追記して報告する

## 最初のタスク（Phase 1 着手点）

「ストレートの弾道積分（重力＋空気抵抗＋マグヌス力）」と「コントロールσによる投球散布」を、
テストファーストで `KokoSim.Engine.Match.Pitching` に実装する。
検証: 145km/h・回転数2200rpmのストレートの到達時間とホップ量が物理的に妥当な範囲に入ること。

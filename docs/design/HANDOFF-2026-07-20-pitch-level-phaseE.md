# 引き継ぎ — 設計書15 Phase E（弾道→swing/contact判定接続・全帯再校正）2026-07-20

> 別チャットで **設計書15 Phase E** に着手するための単一引き継ぎ文書。
> 先に `docs/design/design-15-pitch-level-tactics.md`（§6 Phase E行・§4）と `HANDOFF-2026-07-19-pitch-level-phaseD.md` §8（Phase Dクローズ総括）を読むこと。
> design-01（打席解決6段階）・design-02（能力→物理層変換式・球種）も併読必須。

## 0. 前提（Phase A〜D 完了済み・2026-07-20）

- 全打席・全小技（敬遠/バント/スクイズ/盗塁/牽制/重盗）が `AtBatSession` の毎球ループに統一済み。暴投/パスボール（D-3）も毎球発火。真の1球進行（`AdvancePitch`）実装済み。
- 毎球の実データ `PitchRecord`（球種/コース/球速/`Trajectory?`）が `PlayLogEntry.PitchLog` に載る。**Trajectory は観測専用**（`ctx.CaptureTimeline` ゲート内のみ積分・統計シムはゼロコスト）。
- Sharpness→rpm マッピング確立済み: `SpinRpmBase=2200 / SpinRpmPerSharpness=6.0`（data/coefficients.yaml `pitching.spin_rpm_*`）。
- 現状全緑: !Heavy 633（Skip 1含む）/ Heavy 20。K≈19.1%・BB≈8.2%・HR≈2.84%・得点≈4.1/チーム。**リポジトリはgit管理外**。

## 1. 何をやるか（1行）

Phase B で観測用に載せた弾道（マグヌス変化量・到達時間）を **swing/contact 判定の実入力**にする。「キレのある球は物理的に打ちにくい」を式の抽象値（Stuff/PitchRank直参照）でなく**軌道から創発**させる。不変条件#1（二層構造）への最終忠実化。**全帯再校正込み**＝design-15 最大の再校正イベント。

## 2. 最重要制約（着手前に必ず解くこと）

### 2.1 性能: 毎球RK4積分は統計シムでは使えない（Phase Bの実測事実）

- Phase B 実測: Trajectory積分（RK4×2本/球）を無条件で回すと !Heavy 一式が **67s→120s超** に激増。だから観測は `CaptureTimeline` ゲートに隔離した。
- **Phase E は判定に弾道特徴量が要る＝統計シムでも毎球必要**。ゲート隔離という逃げ道が使えない。
- 対策候補（**着手時に方式を提案して合意を取ること**）:
  - (a) **事前計算テーブル＋補間**: 弾道は (球種, 球速, rpm, リリース) の少パラメータ空間から決定論的に決まる。量子化グリッドで事前積分し、実行時は補間参照。積分器はテーブル生成とゴールデンテストの参照実装として残す
  - (b) **閉形式近似**: 変化量・到達時間を解析近似式にし、積分器と突き合わせテストで精度保証
  - (c) 判定に使う**特徴量だけ**軽量計算（フル軌道は不要。誘発変化量2軸＋到達時間程度なら1回の簡易積分/近似で済む可能性）
- 合否基準: 統計シムの試合あたり時間が現状（≈10.6ms/game）から**大きく劣化しない**こと（目安2倍以内）。日常テスト10秒級の運用（CLAUDE.mdコマンド欄）を壊さない。

### 2.2 モデル設計は事前合意（no-unilateral）

`ContactModel`/`BatterDecision` をどう弾道入力化するかは設計の裁量が大きい。**実装前に「どの判定に・どの特徴量を・どう効かせるか」の案を短く提示して合意を取る**こと。たたき台:
- **空振り率（ContactModel）**: 誘発変化量（縦/横）と到達時間（反応猶予）を whiff 確率の入力へ。現行の Stuff/PitchRank 直参照を置換 or 弾道由来の「実効キレ」へ写像
- **打者判断（BatterDecision）**: 「見え方と実着弾のズレ」（無回転軌道と実軌道の差＝PitchSimulatorが既に2本積分で出している値）をボール球スイング（チェイス）/ 見逃しの誤判定に接続
- **打球質（ContactModelのインプレー生成）**: スコープに含めるか否か自体を提案に含める（最小構成なら空振り/判断のみで、打球生成は現行維持も可）
- 表示層→物理層の変換式は **coefficients.yaml 駆動の一箇所に集約**（不変条件#1/#4）。バランス調整でC#を書き換えない

## 3. 段階計画（スライス・各段で全緑に戻す）

| スライス | 内容 | 帯/digest |
|---|---|---|
| **E-1 特徴量基盤** | 性能対策（§2.1で合意した方式）を実装し、毎球の弾道特徴量を判定パイプラインから**参照可能**にする（まだ判定には使わない）。積分器との一致テスト＋性能実測 | **どちらも不変**（参照だけ・判定不変） |
| **E-2 空振りモデル置換** | ContactModel の whiff を弾道特徴量ベースへ。物理妥当性テスト（変化量大→空振り率単調増、球速大→到達時間短→空振り率増 等） | **全帯が動く**。K%を既存帯へ着地させる係数校正→digest全カード再ベースライン |
| **E-3 打者判断接続** | BatterDecision のチェイス/見逃しへ「見え方と実軌道の差」を接続 | 帯再校正（BB%/K%中心）→再ベースライン |
| **E-4 締め** | Balance CLI 10000試合×複数seedで全帯着地確認。ビフォーアフター報告（K/BB/HR/得点＋球種別空振り率・チェイス率）。design-15 に Phase E 完了注記 | 最終確認 |

- E-2/E-3 は「式を置換→係数を回して既存帯（K≈19%・BB≈8%・HR≈2.8%・得点≈4.1）へ**戻す**」が基本。帯の目標値自体は動かさない（動かしたくなったら質問）。
- 打球質への接続（§2.2の3点目）を入れる場合は E-3 の後に独立スライスとして提案。

## 4. テスト・決定論の作法

- **物理妥当性テスト必須**（CLAUDE.md規約「物理計算は既知の値との突き合わせ」）: 単調性（変化量↑→whiff↑）・境界（無回転球で現行相当）・既知値（145km/h 2200rpm ストレートの特徴量）。
- **決定論**: 弾道特徴量はRNG非依存（決定論的導出）。判定式の置換でRNG**消費順**が変わらないよう、ロール回数・順序は現行と同一に保つ設計を優先（同じ箇所で同じ回数引き、確率値だけ変える）。順序変更が不可避なら理由を報告。
- digest再ベースライン手順は HANDOFF-phaseD §4 の確立済み手順を再利用。avg/modern も**今回は動くのが正**（全打席の判定式が変わるため）。ただし「E-1 では全カード不変」が E-1 の合否。
- Trajectory の観測経路（PitchLog/CaptureTimeline）は現状維持。判定用特徴量と観測用フル軌道は別物として扱ってよい（一致テストで整合だけ保証）。

## 5. コマンド

```bash
# dotnet が PATH に無ければ: export PATH="$PATH:/usr/local/share/dotnet"
dotnet test engine/ --filter "Category!=Heavy"            # 日常
dotnet test engine/ -c Release --filter "Category=Heavy"  # 統計回帰（Release必須）
dotnet run --project engine/KokoSim.Balance -- simulate --games 10000 --seed 42 --report out/report.md
bash tools/sync-engine-dll.sh                             # engine変更をUnityへ
```

## 6. 参照

- 設計: `design-15-pitch-level-tactics.md` §6 Phase E 行＋着手決定注記（2026-07-20）
- 物理資産: `Match/Pitching/PitchSimulator.cs`（2本積分＝誘発変化量）・`BallisticIntegrator.cs`・`PitchTrajectory.cs`・`PitchSpec.cs`
- 現行判定: `Match/AtBat/` の `ContactModel`・`BatterDecision`（置換対象）・`AtBatSession`（呼び出し元）
- 変換式の置き場: `data/coefficients.yaml`（Sharpness→rpm は `pitching.spin_rpm_*` 済み）
- Phase D 総括: `HANDOFF-2026-07-19-pitch-level-phaseD.md` §8（実測値・digest手順・地雷集）
- 帯: `data/balance-targets.yaml`（K/BB/HR/得点/wild_pitch_per_game 等）

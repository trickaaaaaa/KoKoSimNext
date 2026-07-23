# design-20 ドラフト（NPB指名・ドラフト候補・注目度）

> 対応Issue: #178。目的は「甲子園を目指す」の先に「教え子をプロへ送り出す」という長期の
> 育成モチベーションを与えること。注目度の上下を通知フィードで追える＝眺めてニヤニヤできる要素
> （UI原則⑥）にする。

本書は **engine 実装（②）と設計（①）の確定版**。Unity 実装（③＝通知フィード連動・ドラフト画面）は
本書の情報設計に従って別PRで着手する（画面はワイヤーフレーム3案を提示してから組む＝UI-BUILD-METHOD）。

---

## 1. 概念（別概念として厳密に分ける）

| 概念 | いつ | 対象 | 何が起きる |
|---|---|---|---|
| **ドラフト候補（Candidate）** | 在学中いつでも | 1〜3年の全員が対象 | 能力＋成績で算出する**注目度**が閾値を超えると「候補入り」。注目度は成績・能力に応じて随時上下する。候補入り／注目度変化を通知フィードへ流す |
| **ドラフト指名（Nomination）** | **引退後の10月最終週に一度だけ** | **3年生のみ** | 候補ステータスと注目度をもとに、指名有無・指名順位を確定する。確定を通知フィードへ流す |

- 候補ステータスは「注目されている」という**予想・見込み**。確定情報ではない。
- 指名は年に1回の**確定イベント**。3年生は夏（第17週）に引退済みだが、卒業（4月）までロスターに残る
  （`DevelopingPlayer.IsRetired=true` かつ在籍）ので、10月最終週に評価対象として引ける。

## 2. 注目度スコア（Notability）

### 2.1 位置づけ（二層構造との関係）

注目度は **打席・打球・守備の解決に使う確率ではない**＝不変条件#1（二層構造）の対象外。
プロのスカウトが付ける「評価スコア」であり、能力と実績の加重和でよい。
ただし issue の要請「**表示能力値を直接使わず**、成績集計と隠しポテンシャルの扱いを定義」に従い、
表示能力の丸写しを避けるため、

1. 能力は**役割別の合成値**（打者/投手で異なる加重）にする。単一の表示能力を直挿ししない。
2. 能力合成に**隠し上限（才能キャップ `DevelopingPlayer.Cap`）を混ぜる**＝プロは「伸びしろ・天井」を見る。
3. 実績（`Stats`集計）を対等に加重する。

### 2.2 算出式

```
Notability = clamp( wAbility · AbilityScore + wPerf · PerfScore , 0 , 100 )
```

- 既定 `wAbility = wPerf = 0.5`（`data/draft.yaml` で調整）。

**AbilityScore（0〜100）** ＝ 役割別合成（現在値と隠し上限のブレンド）:

```
cur = Σ wk · Level(k)            # 役割別の能力加重
cap = Σ wk · Cap(k)              # 同じ加重で隠し上限を合成
AbilityScore = (1 - ceilW) · cur + ceilW · cap     # 既定 ceilW = 0.35
```

- 打者加重（合計1.0）: Contact .25 / Power .25 / Speed .15 / Fielding .15 / ArmStrength .10 / Catching .10
- 投手加重（合計1.0）: Velocity .40 / Control .25 / Stamina .15 / PitchRank .20
  （＝用語集 PitcherComposite と同一。球速単独で決まらない）
- 役割は `DevelopingPlayer.IsPitcher` で分岐。

**PerfScore（0〜100）** ＝ 実績（`PlayerStatStore.Official.Get(Id)` の累積）。サンプル数で中立50へ収縮:

```
打者 raw = 50 + (OPS - opsBase)·opsScale + hrPerGame·hrScale
投手 raw = 50 + (eraBase - ERA)·eraScale + (K9 - k9Base)·k9Scale
shrink = clamp( sample / minSample , 0 , 1 )      # 打者 sample=PA, 投手 sample=BattersFaced
PerfScore = clamp( 50·(1 - shrink) + raw·shrink , 0 , 100 )
```

- `opsBase=0.700 opsScale=45 hrScale=6`（打者）／`eraBase=3.50 eraScale=6 k9Base=7 k9Scale=2`（投手）。
- 出場がない選手（`Stats` 未登録）は PerfScore=50（中立）＝能力だけで評価される。
- サンプル収縮で「1打席の固め打ち」が過大評価されない（打者 `minSample=40`打席／投手 `minSample=60`打者）。

いずれも `data/draft.yaml` 駆動（不変条件#4）。C# 既定（`DraftCoefficients`）が Unity 実プレイの真値、
YAML は sim/テスト調整用（既存の sim-vs-unity 係数分割に倣う）。

## 3. 候補判定と指名順位予想

### 3.1 予想順位バンド（注目度→予想）

| Notability | バンド | 予想指名順位 | 表示文言 |
|---|---|---|---|
| ≥ 86 | `FirstRound` | 1位候補 | 「1位指名が有力」 |
| ≥ 76 | `UpperRound` | 上位（2〜3位） | 「上位指名圏」 |
| ≥ 66 | `MiddleRound` | 中位（4〜6位） | 「中位指名圏」 |
| ≥ `CandidateThreshold`(58) | `LowerRound` | 下位・育成 | 「下位／育成で名前が挙がる」 |
| < 58 | `None` | 圏外 | （候補でない） |

- **候補入り＝ Notability ≥ CandidateThreshold(58)**＝予想が `None` 以外。学年を問わない。
- 閾値・バンド境界は `data/draft.yaml` 駆動。

### 3.2 指名の確定（10月最終週・3年のみ）

対象は「3年生（`Grade>=3`）」で「候補（バンドが `None` 以外）」の選手だけ。
各選手について、注目度からロジスティックで指名確率を出し、注入乱数で確定する（スカウトの不確実性）:

```
p = 1 / (1 + exp( -(Notability - nomMid)/nomSpread ))   # nomMid=64 nomSpread=6
nominated = rng.NextDouble() < p
round = バンド→代表順位（FirstRound→1, UpperRound→2, MiddleRound→4, LowerRound→6）
```

- 乱数は `IRandomSource` を注入（不変条件#2）。**独立ストリームを Fork** して既存の決定論baselineを乱さない
  （CareerEngine の autumn-flow 前例に倣う。キーは年/週 XOR のマジック定数）。同シード同結果。
- 指名漏れ（`nominated=false`）も結果として残す＝「候補だったが指名されなかった」も物語。

## 4. カレンダー接続点

- 10月最終週＝**週インデックス28**（`SeasonCalendar`：4月始まり50週、10月＝週25〜28）。
  `SeasonCalendar.DraftWeek=28` / `IsDraftWeek(week)` を追加（表示・スケジュールのアンカーのみ＝帯不変）。
- 週送りループ（Unity Shell / SeasonEngine 週ループ）で `IsDraftWeek` の週に `DraftEngine.RunNomination` を
  1回だけ呼び、結果を通知フィードへ流す（③）。指名は独立Fork乱数なので試合結果・帯に影響しない。

## 5. 通知フィード（③・Unity側）

`Assets/KokoSim/Home/HomeState.cs` の `FeedItem`/`FeedKind` を使う（既存 `Discover` 種別あり）。

| イベント | FeedKind | 文言（例） |
|---|---|---|
| 候補入り | `Discover` | 「○○がドラフト候補に名前が挙がった（{バンド文言}）」 |
| 注目度上昇 | `Up` | 「○○の評価が上がった（{旧バンド}→{新バンド}）」 |
| 注目度下降 | `Warn`/`Normal` | 「○○の評価が下がった（{旧バンド}→{新バンド}）」 |
| 指名確定 | `Discover` | 「○○が{球団}から{round}位指名を受けた！」／「○○は指名されなかった」 |

- 候補入り／注目度変化の**差分検出は Unity側（Shell）の責務**。engine は「その週の評価スナップショット
  （選手Id→Notability/バンド）」を純関数で返す。Shell が前回スナップショットと比較して Feed へ流す。
  ＝ engine はセーブスキーマを持たない（保持は Shell 側。破壊的変更を避ける）。

## 6. ドラフト画面（③・IA。着手前にワイヤー3案を提示）

- **候補選手一覧**（在校の候補＝1〜3年）: 名前（明朝）・学年・ポジション・注目度バンド（RankChip 相当の色チップ）・
  予想順位文言・主要成績（打者OPS/HR、投手ERA/K）。数字は Oswald 右揃え（書体3役）。
- **指名実績**（過年度の指名確定＝教え子がプロへ）: 年・名前・round・（将来）球団。キャリアの勲章として残す。
- 部品は RankChip / StatBar3 等の部品辞書内で構成（UI原則⑤）。新規見た目が要るなら部品辞書に追加してから。

## 7. スコープと帯不変の確認

- **② engine（本PR）**: 純C#・決定論・表示専用。指名は独立Fork乱数。既存の match/pitching/running 解決や
  `data/balance-targets.yaml` に一切触れない＝**バランス帯に影響しない**。
- **③ Unity（別PR）**: 表示のみ。フィード投入・画面。engine DLL 同期が要る（夜間ランではDLLをコミットしない）。

## 8. 未確定・将来拡張

- 球団（12球団）割り当て・競合抽選（同一選手に複数球団入札→抽選）は将来拡張。本書では round のみ確定。
- 大学・社会人進路、育成契約と支配下の区別は将来拡張。
- 注目度の全国比較（他校候補とのランキング）は裏ロスター評価が要るため将来拡張。

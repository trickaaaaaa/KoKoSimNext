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

1. 能力は**軸別の合成値**（打者/投手で異なる加重）にする。単一の表示能力を直挿ししない。
   どちらの軸で見るかは選手の属性ではなくスカウトの見立てなので、両方で合成して高い方を採る（§2.2）。
2. 能力合成に**隠し上限（才能キャップ `DevelopingPlayer.Cap`）を混ぜる**＝プロは「伸びしろ・天井」を見る。
3. 実績（`Stats`集計）を対等に加重する。

### 2.2 算出式

```
Notability = clamp( wAbility · AbilityScore + wPerf · PerfScore , 0 , 100 )
```

- 既定 `wAbility = wPerf = 0.5`（`data/draft.yaml` で調整）。

**AbilityScore（0〜100）** ＝ 投手評価と打者評価の**高い方**（現在値と隠し上限のブレンド）:

```
cur = Σ wk · Level(k)            # 役割別の能力加重
cap = Σ wk · Cap(k)              # 同じ加重で隠し上限を合成
AbilityScore = (1 - ceilW) · cur + ceilW · cap     # 既定 ceilW = 0.35
```

- 打者加重（合計1.0）: Contact .25 / Power .25 / Speed .15 / Fielding .15 / ArmStrength .10 / Catching .10
- 投手加重（合計1.0）: Velocity .40 / Control .25 / Stamina .15 / PitchRank .20
  （＝用語集 PitcherComposite と同一。球速単独で決まらない）
- **両方の加重で合成し、高い方を採る**（2026-08-02 改訂・issue #366 / #378）。
  旧仕様は `DevelopingPlayer.IsPitcher` で加重表を選んでいたが、**役割はスカウトが見立てるもので
  選手が持つ属性ではない**（設計書01 §1.1a）。二刀流は投打の良い方で評価される。
  どちらの軸で付いた評価かは `DraftEvaluation.EvaluatedAsPitcher` に記録する（表示用）

**PerfScore（0〜100）** ＝ 実績（`PlayerStatStore.Official.Get(Id)` の累積）。サンプル数で中立50へ収縮:

```
打者 raw = 50 + (OPS - opsBase)·opsScale + hrPerGame·hrScale
投手 raw = 50 + (eraBase - ERA)·eraScale + (K9 - k9Base)·k9Scale
shrink = clamp( sample / minSample , 0 , 1 )      # 打者 sample=PA, 投手 sample=BattersFaced
PerfScore = clamp( 50·(1 - shrink) + raw·shrink , 0 , 100 )
```

- `opsBase=0.700 opsScale=45 hrScale=6`（打者）／`eraBase=3.50 eraScale=6 k9Base=7 k9Scale=2`（投手）。
- 出場がない選手（`Stats` 未登録）は PerfScore=50（中立）＝能力だけで評価される。
- サンプル収縮で「1打席の固め打ち」が過大評価されない（打者 `minSample=100`打席／投手 `minSample=60`打者）。
  打者は当初 40 だったが、夏県＋秋の10試合前後で満額に達して一夏の固め打ちがフルに効いたため 100 へ（2026-08-15）。
- **投打の合わせ方は AbilityScore と違い単純な max にしない**（2026-08-02・issue #378）。
  出場が無い側は中立50を返すので、素朴に max を採ると**悪い成績が中立50に隠れて全員が50以上へ
  持ち上がる**（防御率9点台の投手が、打席ゼロなので50を貰って浮く）。先にサンプルの有無で振り分け、
  **両方あれば高い方／片方だけならその軸／どちらも無ければ50**とする。

いずれも `data/draft.yaml` 駆動（不変条件#4）。C# 既定（`DraftCoefficients`）が Unity 実プレイの真値、
YAML は sim/テスト調整用（既存の sim-vs-unity 係数分割に倣う）。

## 3. 候補判定と指名順位予想

### 3.1 予想順位バンド（注目度→予想）

| Notability | バンド | 予想指名順位 | 表示文言 |
|---|---|---|---|
| ≥ 86 | `FirstRound` | 1位候補 | 「1位指名が有力」 |
| ≥ 76 | `UpperRound` | 上位（2〜3位） | 「上位指名圏」 |
| ≥ 66 | `MiddleRound` | 中位（4〜7位） | 「中位指名圏」 |
| ≥ `CandidateThreshold`(58) | `LowerRound` | 下位・育成（8位） | 「下位／育成で名前が挙がる」 |
| < 58 | `None` | 圏外 | （候補でない） |

- **候補入り＝ Notability ≥ CandidateThreshold(58)** かつ **§3.1a の足切りを通過**＝予想が `None` 以外。学年を問わない。
- 閾値・バンド境界は `data/draft.yaml` 駆動。

### 3.1a 候補入りの足切り（カテゴリランク B 以上・2026-08-15）

注目度は「隠し上限のブレンド」と「成績」で持ち上がるため、選手画面で**打撃力 D** と出ている打者が
（足・守備が良く、上限が高く、夏秋に打った）だけで中位圏に届き「4位指名」される実害が出た。
プレイヤーの体感は「指名されるなら少なくともランク B」なので、注目度とは別に**床**を置く:

```
best = max( 打撃力チップ, 投手力チップ )        # PlayerStrengthProfile の現在値合成（隠し上限を混ぜない）
候補入り ⇔ Tier(best) ≥ CandidateMinTier(B) かつ Notability ≥ CandidateThreshold
```

- 判定に使う合成は**選手画面のカテゴリランクチップと同じ式・同じ係数**（`TeamStrengthCoefficients`）。
  ドラフト独自の打者軸（走力・守備込み）ではなく、プレイヤーが見ている「打撃力」「投手力」の文字で切る
  ＝画面で D と出ている選手が指名されることは無い。
- **投打の高い方**で見る（投手力 A・打撃力 G のエースは通る）。
- 床に届かない選手は注目度に関わらず `None`（候補入り通知も出ない）。注目度の値自体は据え置き
  （`DraftEvaluation.Notability`）＝将来の候補一覧画面で「注目度は高いがランク不足」を出せる。
- 判定は `NotabilityModel.MeetsCandidateFloor` の1箇所。`DraftEngine` の3入口（`Evaluate` / `EvaluateRoster` /
  `RunNomination`）はすべて足切り込みの `BandOf(player, n, strength, c)` を通す。
- `CandidateMinTier` は `data/draft.yaml` の `candidate_min_tier`（G〜S）で調整（C# 既定＝B が実プレイの真値）。

> 併せて挙がっていた「甘さ」の要因のうち、①中位一律4位→§3.2 の按分（4〜7位）で対応、③打者の規定打席→100 へ
> （2026-08-15）。②指名確率の中点（64）が中位下限（66）より低く中位候補はほぼ確実に指名される点は
> 「一旦このまま」（owner 判断・2026-08-15）。

### 3.2 指名の確定（10月最終週・3年のみ）

対象は「3年生（`Grade>=3`）」で「候補（バンドが `None` 以外）」の選手だけ。
各選手について、注目度からロジスティックで指名確率を出し、注入乱数で確定する（スカウトの不確実性）:

```
p = 1 / (1 + exp( -(Notability - nomMid)/nomSpread ))   # nomMid=64 nomSpread=6
nominated = rng.NextDouble() < p
round = バンド内で注目度に応じて按分（DraftRankBands.RoundOf）
        FirstRound→1 / UpperRound→2〜3 / MiddleRound→4〜7 / LowerRound→8
        バンドの注目度区間を順位数で等分し、上端ほど上の順位（中位: 76→4位 … 66→7位）
```

- 順位の按分は 2026-08-15 から。以前は「バンドの代表順位」（中位なら一律 4位）だったため、
  中位ギリギリで滑り込んだ候補まで全員「4位指名」と表示され、実力より甘く見えた。
  中位が 7位まで降りるので、下位・育成の順位は必ずその下（8位）に置く。

- 乱数は `IRandomSource` を注入（不変条件#2）。**独立ストリームを Fork** して既存の決定論baselineを乱さない
  （CareerEngine の autumn-flow 前例に倣う。キーは年/週 XOR のマジック定数）。同シード同結果。
- 指名漏れ（`nominated=false`）も結果として残す＝「候補だったが指名されなかった」も物語。

## 4. カレンダー接続点

- 10月最終週＝**週インデックス28**（`SeasonCalendar`：4月始まり50週、10月＝週25〜28）。
  `SeasonCalendar.DraftWeek=28` / `IsDraftWeek(week)` を追加（表示・スケジュールのアンカーのみ＝帯不変）。
- 週送りループ（Unity Shell / SeasonEngine 週ループ）で `IsDraftWeek` の週に `DraftEngine.RunNomination` を
  1回だけ呼び、結果を通知フィードへ流す（③）。指名は独立Fork乱数なので試合結果・帯に影響しない。

> **③の配線（issue #586・実装済み）**: `GameClock.EnterWeek` が毎週 `DraftService.RefreshBands()`（注目度の
> 再評価＝純関数・乱数不要）を回し、`IsDraftWeek` の週だけ `DraftService.RunNomination` を呼ぶ。
> **対象ロスターは `RosterService.Roster`（`Active` ではない）**＝3年生は夏に引退して Active から外れるが、
> 指名は10月なので**引退した3年こそが本命**。評価の母数は**公式戦通算**（`PlayerStatStore.Official`）で、
> 練習試合の成績は公になった実績ではないのでスカウトの評価軸に載せない。

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
  この差分検出用の「前回スナップショット」保持は引き続き Shell 側でよい（＝フィード表示のための一時状態で、
  セーブ本体とは別物）。

> **改訂（issue #507・セーブ基盤 F0, 2026-08-07）**: 旧記述「＝ engine はセーブスキーマを持たない
> （保持は Shell 側。破壊的変更を避ける）」は**撤回する**。**セーブの正典スキーマ（DTO・シリアライザ・
> 版管理）は engine 側 `KokoSim.Engine.Saves` に置く**（#483 で決着）。理由: Shell（Assets）には asmdef も
> テストフレームワークも無く、セーブ→ロードの往復テストと決定論テスト（セーブを跨いでも同シード同結果・
> とくに `NationRosters` の年度跨ぎ同一性）は engine でしか自動化できないため。engine は純C#の DTO と
> シリアライザだけを持ち（`UnityEngine` 非依存・不変条件#3。学校・県は決定論再生成なので保存せず、
> 保持は materialize 済み全国ロスター・自校部員・監督メタ・成績/戦績・母種シード・暦のみ）、gzip 圧縮と
> `Application.persistentDataPath` へのファイル書き出しは Shell（`SaveGameIO`）が担う（＝形は engine・
> 書き出しは Shell）。**大会中も試合と試合の間なら保存できる**（issue #565 F1。`TournamentRunner` の
> 可変状態を `SaveState.Tournament` に載せる。観戦中＝`MatchProgression` を持つ局面だけが対象外）。上のドラフト通知フィードの
> 差分検出が Shell 側なのは変わらない（あれはセーブでなく表示のための前回比較）。

## 6. ドラフト画面（③・IA。着手前にワイヤー3案を提示）

- **候補選手一覧**（在校の候補＝1〜3年）: 名前（明朝）・学年・ポジション・注目度バンド（RankChip 相当の色チップ）・
  予想順位文言・主要成績。数字は Oswald 右揃え（書体3役）。
  主要成績は**出場実績で選ぶ**（登板があれば ERA/K、打席があれば OPS/HR、二刀流は両方）＝
  役割で切り替えない（設計書01 §1.1a、#379 でスタメン画面に入れたのと同じ作法）。
- 部品は RankChip / StatBar3 等の部品辞書内で構成（UI原則⑤）。新規見た目が要るなら部品辞書に追加してから。

> **改訂（issue #586・2026-08-11）**: 旧記述「**指名実績**（過年度の指名確定）もドラフト画面に置く」は撤回。
> **候補一覧＝ドラフト画面（現役選手の話）／指名実績＝キャリアタブの「教え子名鑑」（過去の勲章）** に分ける。
> 候補一覧はドラフトが近づくと毎年見る運用画面、指名実績は監督の歩みの一部で見る頻度も文脈も違う。
> 教え子名鑑は**年・当時の背番号・名前・round**（球団は将来）。

### 6.1 教え子名鑑の実装（issue #586・実装済み）

- 台帳は engine の `Career/Draft/DraftHistory.cs`（`DraftRecord`）。**指名された者だけ**を積む
  （落選は勲章ではない＝指名週の通知フィードにだけ出て消える）。セーブ **v7** の `SaveState.Draft` で永続化する。
- **暦年は導出せず確定時の値をそのまま焼く**。暦年の起点（`SeasonClock.SeasonBaseYear`）は Shell 側の定数で
  engine は知らない＝導出にすると起点を動かした瞬間に過去の勲章の年が全部ずれる。
- **「当時の背番号」は選手本体からは引けない**。3年生は夏の新チーム発足で背番号を返上する
  （`RosterLifecycle.BeginNewTeam` → `UniformNumberAssigner.Clear`・issue #531）のに対し、指名は10月。
  素直に `DevelopingPlayer.UniformNumber` を読むと**必ず 0 になる**。大会別アーカイブを遡る
  `TournamentArchive.LastUniformNumber(sourceId)`（学年の高い順・同学年は夏→秋→春）を唯一の入口にする。

## 7. スコープと帯不変の確認

- **② engine（本PR）**: 純C#・決定論・表示専用。指名は独立Fork乱数。既存の match/pitching/running 解決や
  `data/balance-targets.yaml` に一切触れない＝**バランス帯に影響しない**。
- **③ Unity（issue #586 で指名フック・通知フィード・教え子名鑑まで実装済み。候補一覧画面は未着手）**:
  表示のみ。engine DLL 同期が要る（夜間ランではDLLをコミットしない）。
  週次のバンド差分（候補入り／評価の増減）は **Shell が持ち、セーブには載せない**（表示のための前回比較で
  進行の実体ではない）。ただし**セーブ復元時に基準を採り直す**こと＝採り直さないと読み込み直後の1週目に
  在籍候補が全員「候補入り」として流れる（`DraftService.Restore`）。

## 8. 未確定・将来拡張

- 球団（12球団）割り当て・競合抽選（同一選手に複数球団入札→抽選）は将来拡張。本書では round のみ確定。
- 大学・社会人進路、育成契約と支配下の区別は将来拡張。
- 注目度の全国比較（他校候補とのランキング）は裏ロスター評価が要るため将来拡張。

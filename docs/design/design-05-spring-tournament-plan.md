# 春季大会（県→地区大会）実装計画

> issue [#1793](https://github.com/trickaaaaaa/KoKoSimNext/issues/1793) の成果物。**この文書自体はコードを変更しない**（計画と分割案のみ）。
> 親issue: [#1269](https://github.com/trickaaaaaa/KoKoSimNext/issues/1269)（春季大会が丸ごと未実装）。
> 設計根拠: design-05 §1.1/§1.1a/§1.2/§1.4/§1.5/§1.5b、design-03 §2、design-04、design-20。
>
> 置き場所について: 当初は design-05 §1.1 の下に直接書く案だったが、オーナー判断の反映・既存コードの接続点
> 一覧・損得の数値検証・分割案まで含めると1節に収まらない長さになったため、本ファイルへ分離した。
> design-05 §1.1 からはこのファイルへのポインタだけを置く。

---

## 0. オーナー判断の再掲（2026-11-28）

実装着手前に確定している前提。この計画はこれを所与として組む。

1. **範囲**: 県大会 → **地区大会まで**（関東大会など。秋の地区大会と同じ地区割り）。全国大会は無い
2. **裏で試合させる範囲**: **自校がいる地区の県だけ**（地区内の各県大会＋その地区大会）。他地区は春季大会を行わない
3. **夏のシード**: 春の成績で決める（design-05 §1.2 の「春季大会の実装時に差し替える」を実行）。辞退可・辞退するとノーシード。
   自校の地区以外の県は春季大会が無いので、今どおり `School.Strength` でシードを決める
4. **春の成績の使い道**: 選手の成績・記録室・ドラフトの注目度・名声に**含める**

加えて、design-05 §1.1a には春季大会の任意参加について既に2026-07-21付けの決定が書かれている
（確認ダイアログの文言案・参加時/辞退時の扱い）。本計画はそれをそのまま実装の土台にする。

---

## 1. 日程（年間カレンダーへの配置）

design-03 §2 の年間カレンダー表はすでに「4〜5月 春季大会」と書いているので、新規の週を
どこに置くかだけが論点。`SeasonCalendar`（`engine/KokoSim.Engine/Season/SeasonCalendar.cs`）の
既存定数は週0〜12（4月＝週0-3・5月＝週4-8・6月＝週9-12）に**何も置いていない**
（直近の固定アンカーは `SummerTournamentStartWeek=13`）。このウィンドウに新しい大会モード開幕週を1つ追加する。

- **新定数 `SpringTournamentStartWeek`（県大会の開幕週・既定値案 2）**: 週0は入学・仮入部（新入生の能力が
  「？」のまま）の週なので、1週間だけ間を置いて週2（4月2週目）に開幕する。これは実在の春季大会の
  開催時期感（4月上旬〜中旬）に近い。
- **地区大会（県大会の優勝〜上位校決定後）は暦の週を持たない**。summer の `SummerKoshien` ・秋の
  `Regional`/`Jingu` と同じ理由（design-05 §1.1a/§1.5b）＝暦に開幕週を作ると後続の固定週と衝突しうる。
  春は幸い週2〜週12の間に他の固定アンカーが無いので衝突のリスク自体は低いが、**対称性を取るため同じ
  「後始末から即座連鎖」方式に揃える**（実装の型をRegionalEntry/JinguEntryと合わせられる・新規の型を増やさない）。
- 県大会自体が消費する週数は既存の `TournamentSchedule`（県の出場校数で5〜9ラウンド・最大21日=3週、
  design-05 §1.2）をそのまま使う。最大想定（神奈川・東京級）でも 週2+3=週5 で収まり、地区大会の連鎖分を
  積んでも6月（週9）に入る前に収まる見込み（県大会よりずっと小さい地区大会がさらに日数を使うため、
  実装時に `TournamentScheduleIntegrationTests` 相当で実測して確認する）。6月に食い込んでも致命的な衝突は
  無い（6月はランダムイベント「テスト週」のみで固定週ではない）。
- **週番号は実装時にもう一段検証してよい**（例えば週3に1週遅らせる等）。4〜5月（週0〜8）の範囲内に収める
  ことだけが制約で、具体的な週番号自体は issue 本文で指定されていないため実装時の最小判断とする。
- 表示上の実月日（`TournamentDate`・issue #583方式）は **県大会＝4月中旬開幕、地区大会＝5月開幕** とし、
  `TournamentSchedule` に `SpringOpenMonth/Day`・`SpringRegionalOpenMonth/Day` を追加する
  （既存の `SummerOpenMonth` 等と同型。§5 のチェックリストに反映）。

---

## 2. 大会の定義

### 2.1 `TournamentKind` への追加

`engine/KokoSim.Engine/Season/SeasonCalendar.cs:19-38` の enum へ、コメントにある制約
「**値は末尾にだけ足す**」（セーブへintで直列化されるため）に従って2値を追加する:

```csharp
public enum TournamentKind
{
    Summer, Autumn, SummerKoshien, Regional, Jingu, SenbatsuKoshien, AutumnDistrict,
    Spring,         // 春季県大会
    SpringRegional, // 春季地区大会（県大会の後始末から即座連鎖・暦の週を持たない）
}
```

命名は秋の `Regional`（秋季地区大会）と対にして `SpringRegional` とし、`AutumnDistrict`
（県内の地区予選＝神奈川7地区等）とは紛れない名前にする（両者は階層が違う：
`AutumnDistrict` は県大会**より手前**の県内予選、`SpringRegional` は県大会**より後**の都道府県間大会）。

### 2.2 フォーマットYAML

- **県大会フォーマット**: `data/pref-formats/` に県ごとの定義を置く既存方式（`PrefFormat`・
  `PrefFormatDistrictParser`）を**そのまま再利用**する。ただし **47都道府県の実形式準拠は秋だけの
  「こだわりポイント」**（design-05 §1.5）で、issueは春にその要求を課していない。したがって:
  - Phase 1（本計画のスコープ）は**汎用プリセット**（knockout一本、秋のPhase4初期と同じ考え方）を
    全県の既定にする。実在の春季大会は地区予選を伴わないシンプルなトーナメント形式が大半
    （`data/pref-formats/kanagawa.yaml:19` の既存コメント「春と異なり秋は地区がリーグ戦形式」が示唆する
    とおり）なので、knockout既定は現実にも近い。
  - 47都道府県の実データ収録は秋と同じ並行タスクとして**後続issue化**（本計画の分割案 §7 の末尾に積む。
    必須ではなくbacklog）。
  - 背景シムの対象は自地区内の県だけ（§3）なので、他地区の県フォーマットは**実装時点では使われない**
    （定義自体は47都道府県ぶん要る＝どの県が「自地区」になるかはプレイごとに変わるため）。
- **地区大会フォーマット**: `engine/KokoSim.Engine/Nation/Tournaments/RegionalFormat.cs` と
  `data/pref-formats/regional-tournaments.yaml` と**同じスキーマを再利用**し、新規ファイル
  `data/pref-formats/spring-regional-tournaments.yaml` を作る（`RegionalFormat`/`RegionalFormatSet`・
  `RegionalFormatParser` はそのまま、`ChampionToJingu` 相当のフィールドは春では使わない＝後続大会が無いため
  常にfalse/無視）。
  - **地区割り（10地区）は秋と同じ**（issue本文の指定どおり）。`berths_per_pref`（県ごとの出場枠）の値は
    秋のものをいったん流用する（実在の春季地区大会枠は秋と完全には一致しないが、issueは春の枠配分の
    真正性を要求していない。実測との差分検証は backlog）。
  - 近畿の出場校決定戦（`RegionalPlayoffRule`）・開催県ボーナス（`HostPrefectureBonus`）は**春では採用しない**
    （Phase 1はシンプルな固定枠。近畿の特殊処理は秋固有の2026年決定でissue化されたもので、春に同じ
    複雑さを持ち込む要求は無い）。

### 2.3 既存の `switch (TournamentKind)` 一覧（新ケースの要否）

実装時にこぼさないよう、調査で見つけた分岐点をまとめる（Explore調査・2026-11-28時点）。

| ファイル:行 | 現状 | 新ケースの要否 |
|---|---|---|
| `engine/.../Season/TournamentKindNames.cs:13-19` | kind→表示名の `switch` | **要る**（「春」「春季地区大会」等を追加。無いと例外） |
| `engine/.../Nation/Tournaments/CrowdTurnout.cs:27-35` | kind→観客の入り3段 | **要る**（無いとdefault分岐に落ちて不自然な入りになる可能性。県大会は`Autumn`相当・地区大会は`Regional`相当を流用でよい） |
| `engine/.../Nation/Tournaments/TournamentPressure.cs:24,30` | kind→プレッシャー指数ボーナス | **要る**（春は「夏の甲子園ほどの重さは無い」位置づけなので`Regional`/`Jingu`と同等の中位ボーナスが妥当） |
| `Assets/KokoSim/Shell/MatchStadium.cs:44-60` | kind→球場ティア割当 | **要る**（design-05 §3「大会→球場の割当」に春を追記。序盤=市営級・準決勝決勝=県営級、`Summer`/`Autumn`と同じ`RegionalStadiumTier.ForStadium`経路を流用） |
| `engine/.../Season/TournamentDate.cs:38-45` | kind→実開幕月日 | **要る**（`_`の既定が`Autumn`の月日を返すため、追加しないと春の大会が「秋に開幕」と表示される） |
| `engine/.../Nation/Tournaments/KnockoutPlacement.cs:15-16`（`ThirdPlaceRule.AppliesTo`） | kind→3位決定戦の有無 | **判断要**（design-05 §1.5の3位決定戦は秋季大会向けの決定。春に同じ体験を入れるかはオーナー確認が要る＝§6未決事項） |
| `Assets/KokoSim/Shell/GameSession.cs:1204` 周辺（`NationService.YearLedger.AddMatches`） | kindを問わず常時AI校進化のwinsBySchoolへ積む | **要る（除外ガード）**＝§3で詳述。これが本計画でいちばん重要な発見 |
| `engine/.../Nation/SchoolRecordBook.cs:147-166`（`FoldTournament`） | koshien/Summer以外は汎用の`RecordOfficialMatch`のみ | **不要**（既に汎用。春も自動的に正しく積む） |
| `engine/.../Nation/Tournaments/TournamentRunner.cs:1402`（`MercyRuleEnabledFor`） | `_isNationalTournament`フラグと決勝判定だけで決まる | **不要**（春は`isNationalTournament:false`で呼べば既存ロジックのまま「決勝以外コールドON」になる） |
| `Assets/KokoSim/Shell/RegionalEntry.cs`／`JinguEntry` | 秋の「後始末から即座連鎖」の実装パターン | 新規 `SpringEntry`/`SpringRegionalEntry` を**同型で新設**（既存改修ではなく追加） |
| `Assets/KokoSim/Tournament/TournamentPreviewController.cs` | 大会展望の表示分岐 | **要る**（春の大会名・展望カードを出す分岐を追加） |

---

## 3. 裏試合（自地区限定フルシム）と「AI校進化への波及」の扱い

### 3.1 絞り込みの単位

秋の地区大会の実装パターン（`DistrictEntry.StartOtherDistrictSim`・`RegionalNationEngine.RunOtherRegions`
`engine/KokoSim.Engine/Nation/Tournaments/RegionalNationEngine.cs:42-99`）が「自地区だけ回す／他地区を
除外する」絞り込みそのものに近い。地理区分は `engine/KokoSim.Engine/Nation/PrefectureRegions.cs`
（`RegionOf`）と `data/pref-formats/regional-tournaments.yaml` の `region:` を使う（夏専用の
`SummerRegions.RegionFor` とは無関係＝混同しない）。

- 自校の都道府県が属する地区（例: 関東）に含まれる都道府県（茨城・栃木・群馬・埼玉・千葉・神奈川・山梨・
  東京）**だけ**、県大会をフルシムする（§1.4の全国夏フルシムと同じ `GameEngine.Play` 経路・永続ロスター）。
- その地区の県大会で各県の出場枠ぶんの代表が決まったら、地区大会もフルシムする。
- 他地区（東北・北信越・東海・近畿・中国・四国・九州・北海道のうち自校が属さない地区）は
  **春季大会を一切回さない**＝それらの県の学校は今年度「春の成績」を持たない（Noneのまま）。
  シード算出（§4）・成績アーカイブ（§5）の双方でこの「無い」状態を素通りできるようにする。

### 3.2 AI校の年次進化（`AiSchoolModel.Evolve`）への波及（要注意の発見）

調査で判明した重要な接続点: `Assets/KokoSim/Shell/GameSession.cs:1204` が
`NationService.YearLedger.AddMatches(matches)` を**大会の種別を問わず常に**呼んでおり、
これが `AiSchoolModel.Evolve`（`engine/KokoSim.Engine/Nation/AiSchoolModel.cs:14-49`）の
`winsBySchool`（校ごとの当年勝利数→強さ・名声の増分）の材料になる。

この経路へ `Spring`/`SpringRegional` の試合結果を無条件で素通りさせると、**自校が属する地区の
AI校だけが「春ぶんの勝利」を毎年余分に受け取り、年次進化で強さ・名声が他地区のAI校より系統的に
速く伸びる**。これは「県ID・地区は強さの式に入らない」（design-05 §2.2a）という既存の不変条件に反する
非対称バグになる（他の地区大会接続＝`AutumnDistrict`は全国の districted 県で対称に回っているので
この問題を起こさない。`Regional`/`Jingu`も47都道府県全部が背景で回る前提なので対称）。

- **対処（本計画で確定する実装方針）**: `GameSession` の当該フック（`EndTournament` 相当）に
  `kind is not (TournamentKind.Spring or TournamentKind.SpringRegional)` のガードを追加し、
  **春季大会の勝敗はAI校進化の `winsBySchool` 集計から除外する**。
- 一方、**自校自身の名声（`FameUpsetModel`・`ManagerService.RecordTournamentUpsets`）・選手成績
  （`PlayerStatStore`）・注目度（`NotabilityModel`）は校ごとに独立した計算**（全国対称性の前提を
  持たない）なので除外不要＝オーナー判断4「春の成績を名声・成績・注目度に含める」はそのまま満たされる。

---

## 4. シード算出の差し替え（夏のシード）

現状は `engine/KokoSim.Engine/Nation/Tournaments/TournamentDraw.cs:55-56,65` に明記のとおり
「シードの根拠は当面 `School.Strength`（春季大会が未実装のため総合力で代用。春季大会の実装時に差し替える）」
となっている。同じ並びのロジックが `engine/KokoSim.Engine/Nation/Tournaments/TournamentPreview.cs:120,191,218,227`
にも重複している（大会展望のシード表示）。

- **自校の地区の県**: 夏のシード根拠を「春季大会の成績（最終順位）」に差し替える。順位の定義は
  `KnockoutPlacement`（design-05 §1.5bで秋の地区大会枠判定に使っているのと同じヘルパー）を再利用できる。
- **自校の地区以外の県**: 春季大会が存在しないので、**今どおり `School.Strength` を使う**
  （オーナー判断3の後段そのまま）。つまり `TournamentDraw.Arrange`／`TournamentPreview` のシード根拠は
  「春季大会の順位があれば順位、無ければ強さ」の二択関数に差し替える（既存コードの4箇所を洗い出して
  同じ関数を通す）。
- 春季大会を**辞退**した自校・自地区内の他校（AI校は辞退しない。§5参照）は「春季大会の順位が無い」
  扱いになり、同じ二択関数で `School.Strength` 側へ自然に落ちる＝ノーシード（オーナー判断3）と整合する。

---

## 5. 辞退（参加確認ダイアログ・ノーシード）

design-05 §1.1a に既に以下が決定済みなので、本計画はこれをそのまま実装の仕様にする（新たな判断は増やさない）:

- 大会エントリー時（春季大会開始前の週ターン＝`SpringTournamentStartWeek` 直前）に参加確認ダイアログを表示する。
  文言案（最終はUI実装時に確定）: 「春季大会に出場しますか？　※優勝しても甲子園には出場できませんが、
  夏の大会のシード権に直結し、選手が実戦経験で成長します。」
- 参加時: 夏のシード権（§4）＋実戦成長（`MatchGrowthModel`）・成功体験ボーナス・今日のMVP（§6）
- 辞退時: 夏はノーシード扱い。該当週は通常の週ターン（練習）として進行。**追加ペナルティは設けない**
- AI校は辞退しない（裏試合は常に全参加）。自校のスキップは他校のシード算出に影響しない

実装は夏・秋の「大会モードへ入る／入らない」の分岐と同じ骨格（`TournamentEntry`系のクラス）に、
春だけ「入る前にプレイヤーへ確認を挟む」フックを足す形になる。

---

## 6. 成績・記録・注目度・名声への接続（オーナー判断4）

Explore調査の結果、**多くの経路は既に大会種別に依存しない汎用実装**で、新規の分岐をほぼ要さない:

- **選手成績（`PlayerStatStore`・`NationTournamentStats`）**: 試合が `isOfficial: true` で畳み込まれれば
  `Official`（公式戦通算）に自動で乗る。大会別アーカイブ用のスコープを増やすなら、秋季地区大会の
  「焼き取り」スコープ（`RegionalQualifier`/`RegionalQualifierForSchool`、design-05 §1.5bと同型）に倣って
  `SpringQualifier`/`SpringRegionalQualifier` を追加できる（春季大会の個人成績を独立に見たい場合のみ。
  必須ではなく、記録室UIの要望次第でPhase 2以降に回してよい）。
- **ドラフト注目度（`NotabilityModel`）**: 大会種別フィルタを持たない（`PlayerStats`を渡すだけ）ので、
  春季大会の成績が `Official` に乗った時点で自動的に反映される。追加改修は基本不要。
- **名声（`FameUpsetModel`）**: 大会種別を区別しない純関数。春季大会の`TrackedMatch`を年度末の集計へ
  渡せば、秋季地区大会の勝利と同じ式で金星/取りこぼしが乗る。**ただし§3.2のAI校進化除外とは別物**
  （こちらは自校の名声のみなので除外不要）。
- **今日のMVP（`MatchMvp`）・成功体験ボーナス（`MatchGrowthModel.ApplySuccess`）**: 大会の格倍率
  `TrainingCoefficients.MatchSuccessStageMult(TournamentKind?)`（`engine/.../Season/TrainingCoefficients.cs:205-211`）
  に `Spring`/`SpringRegional` のケースを追加しないと、switch の `_`（県大会相当=1.5倍）に落ちる。
  県大会は1.5倍のままで実害ないが、**地区大会（`SpringRegional`）は明示的に`Regional`/`Jingu`と同じ2.0倍へ
  switch を拡張する**（しないと地区大会なのに県大会と同じ1.5倍になり格の表現が崩れる）。
- **記録室（`RecordRoom`）**: 既存の「順位付け・規定打席」が大会名を問わず成績から再構成する作りなら
  追加改修は不要（design-05 §1.4a/§1.4bの実装パターンを踏襲すれば同様になるはず。詳細は実装issueで確認）。

---

## 7. セーブ（版数・移し替え）

- 最新セーブバージョンは **v53**（`engine/KokoSim.Engine/Saves/SaveCodec.cs:44`）。本機能はセーブへ
  以下を新規に持たせる必要があるため、**v54へ上げる**:
  - 自校の「今年度・春季大会に参加したか／していないか」フラグと、参加した場合の順位・結果
    （シード算出・歴代大会結果・辞退UIの再表示抑制に使う）
  - 地区大会進出の有無・結果（参加した場合のみ）
- マイグレーションは既存の `Migrate`（`SaveCodec.cs:280`〜の `if (version < N)` 積み上げ）に1段追加。
  旧セーブ（v53以前）は「春季大会フラグ無し＝今年度は未参加/大会期間外」として読み込む
  （4〜5月のタイミングで旧セーブをロードした場合の救済は実装issueで詳細化。シーズン途中ロードの
  一般論として他の大会種別も同様の救済パターンを持つはずなので、それに合わせる）。

---

## 8. 統計帯（Heavy）の扱い

- **既存の帯（`data/balance-targets.yaml`）は `TournamentRunner` を通る試合結果を測っていない**
  （design-05 §1.2「既存の帯は`TournamentRunner`を1本も通らない」）ので、春季大会追加そのものが
  既存バランス帯を直接動かす可能性は低い。
- 一方で春季大会は以下を**実際に動かす**ため、確認が要る:
  - **AI校の年次進化**（`AiSchoolModel.Evolve`）: §3.2のガードが無いと自地区AI校だけ強さ・名声が
    突出する＝`nation_dominance`系の帯（Q24-3）や「県ID は強さの式に入らない」不変条件への抵触リスク。
    ガードを入れた上で、実装PRでは**自地区のAI校が他地区と比べて強さ分布で突出しないこと**を
    `AbilitySensitivityTests`相当か簡単な多年シムで確認する。
  - **実戦成長（`MatchGrowthModel`）**: 春季大会で追加された出場機会ぶん、精神力・走塁判断・捕手リードの
    伸びが増える。既存の3年成長目安（design-02）を大きく超えないか、`AiRosterCalendarGrowthTests`相当の
    帯で確認。
  - **名声・注目度**: 自校のキャリア帯（`trial-start-strength-measurement`メモリ等で既に計測している指標）
    に春季大会参加が加わることで年数あたりの伸びがどう動くか、既存のキャリア計測スクリプトで実測。
  - これらは**実装PR側で**`bash tools/needs-heavy.sh`判定に従い Heavy を回して確認する（本計画書自体は
    コード変更をしないため今は実行しない）。

---

## 9. 出場/辞退の損得の検証（オーナー依頼の数値比較）

オーナーが「計画書で確かめてほしい」と指定した論点: 「時間を取られる」ことが本当に損になっているか。
実装前の机上計算で、既存の係数値（`engine/KokoSim.Engine/Season/TrainingCoefficients.cs`）を使って見積もる。

### 9.1 「時間を取られる」側（コスト）

1. **練習効率の低下**: `TournamentPracticeMult = 0.5`（`TrainingCoefficients.cs:134`）。大会モード中の
   週は**練習が丸ごと止まるのではなく半減**する（`Assets/KokoSim/Home/HomeState.cs:1075`
   `RunPracticeWeek(_training.TournamentPracticeMult)`）。これは夏・秋の大会でも既に起きている既存コストで、
   春固有の新しい負担ではない。1メニューの週あたり主効果 `BaseMainExp=290` が `145` に落ちる＝
   県大会＋地区大会の合計2〜4週（§1）ぶんで、1種目あたり概算 `145×(週数)` のexpが失われる
   （3年通算 +16.4 の目安（design-02実測コメント、`TrainingCoefficients.cs:11`）に対し、290expは
   おおよそ0.3〜0.5レベル相当。**数値としては小さい**）。
2. **怪我リスクの増加**: 試合中の場面駆動の受傷判定（死球・本塁クロスプレー・フェンス激突・スライディング・
   投球過多、design-03 §3.5）は**試合数に比例して発生機会が増える**。大会に出れば出ないより確実に
   怪我のチャンスが増える＝これは「ペナルティ」ではなく実在するリスクコストとして効く。
3. **名声の下振れリスク**: `FameUpsetModel.MatchDelta`（`engine/.../Career/FameUpsetModel.cs:21-27`）は
   格下に負けると `FameUpsetLossPerTier=3.5`×格差ぶん名声が**下がる**（上昇側だけキャップがあり下降側は
   無制限）。春は新入生がまだ「？」のまま・新チームの力関係も固まっていない時期なので、**格下だと思って
   いた相手に取りこぼす事故のリスクが通常の大会より高い**（実力の見積もり誤差が大きい時期）。
   辞退すれば試合が無い＝このリスクを完全に避けられる。

### 9.2 出場する側（ベネフィット）

1. **実戦成長（`MatchGrowthModel`）**: 精神力・走塁判断・捕手リードは**公式戦/練習試合の出場でしか
   伸びない**（`MatchGrowthModel.cs:8`のコメント、design02 §5.3a）。春季大会で出場機会が増えた分だけ
   この3能力が確実に伸びる。特に**控え選手の見極め**（オーナーの挙げたメリット）に直結＝控えは普段
   公式戦に出られないので、春季大会が無いと「出場でしか伸びない」能力を伸ばす機会そのものが来ない。
2. **成功体験ボーナス**: 大会の格倍率が練習試合の1.0倍に対し、県大会は1.5倍・地区大会は2.0倍
   （§6で `SpringRegional` を `MatchSuccessStageMult` に追加する前提）。
3. **今日のMVP**（`MatchMvp`）: 公式戦に**勝った**試合限定で、活躍した1人へ成功体験ボーナスの3倍
   （`MvpRewardMult=3.0`）の経験値。練習試合では発生しない＝公式戦ならではの上澄み。
4. **名声の上振れ**（金星）: 格上に勝てば `FameUpsetWinPerTier=3.0`×格差ぶん名声が上がる
   （上昇側は`FameUpsetSeasonCap=8.0`で年間頭打ちだが、0ではない）。
5. **夏のシード**（§4）＋**注目度・成績アーカイブ**（§6）。

### 9.3 結論（この段階での見立て）

- 単純に「出場すれば常に得」とは言えない。**名声は上振れ/下振れの両方があり**（9.2-4と9.1-3が対称）、
  **怪我リスクは出場側の純粋なマイナス**。実戦成長・MVP・成功体験ボーナスは出場側の確実な加点だが、
  それらは「精神力・走塁判断・捕手リード」「成功体験の経験値」という**練習では代替できない専用の
  成長経路**であって、練習週の主効果（ミート・パワー等）とは別の資源なので「練習半減の損」と単純な
  天秤には乗らない（比べるものが違う）。
- したがって**現行design（辞退に追加ペナルティを設けない。design-05 §1.1a）のままでも、選択として
  機能するだけの実コスト（怪我リスク・名声下振れ）と実ベネフィット（専用成長経路・シード・名声上振れ）
  が両側に揺らぎとして既に存在する**、という机上の結論になる。
- ただし**これは実装前の机上計算であり、実際のシミュレーションでの検証はできていない**
  （本issueはコードを書かない計画issueのため）。控えの出場頻度・名声の変動幅・怪我の発生率は実際の
  AI采配・ロスター構成に強く依存し、数字を鵜呑みにできない。**よって「釣り合いが取れているか」の最終
  確認は、実装後に Balance CLI／Heavyで実測してから判断する課題として OPEN-QUESTIONS に残す**（§10）。

---

## 10. 未決事項（`docs/design/OPEN-QUESTIONS.md` へ追記する内容の下書き）

実装前に機械的には決まらない論点。OPEN-QUESTIONS.md本体には要約を追記し、詳細はこのファイルを指す。

1. **出場/辞退の釣り合いの最終検証**（§9.3）: 机上では両側に揺らぎがあるが、実測では「出場が明確に
   優位」または「辞退が明確に優位」へ傾く可能性がある。実装PRで Balance CLI（複数年キャリアシム・
   出場固定/辞退固定の2条件比較）を回し、Fame・シード経由の夏成績・怪我発生率の差を数値化する。
   - 案A: 傾きが軽微（既存の怪我リスク・名声リスクで十分吸収）→現行設計のまま確定
   - 案B: 出場が明確優位→辞退側に小さな代替ベネフィット（例: 辞退した週は`TournamentPracticeMult`の
     ペナルティが無い通常練習＝1.0倍のまま、になる現仕様をさらに活かし、辞退時だけ個別指導3枠の
     効果を上げる等）を検討
   - 案C: 辞退が明確優位（シード無しでも大きな支障が無い）→シードの価値（ノーシード時の初戦相手の強さ
     分布）を強める
2. **3位決定戦（`ThirdPlaceRule`）を春季の県大会・地区大会にも適用するか**: design-05 §1.5の3位決定戦は
   「秋季県大会・秋季地区予選・秋季地区大会」向けの2026-11-23決定で、夏・甲子園・神宮は対象外と明記
   されている。春は言及が無い。センバツに直結しない大会なので3位の意味は薄いが、記録上の体裁（4位との
   区別）は秋と揃えたほうが一貫する。
3. **47都道府県の春季大会フォーマットの実データ収録**: 秋のような「本作のこだわり」対象に含めるか、
   汎用プリセットのままで良いか（§2.2のPhase 1は汎用プリセット前提）。
4. **春季大会用の成績スコープ（`SpringQualifier`等）を独立に設けるか**: §6で触れたとおり必須ではない。
   記録室UIで「春季大会の個人成績」を単独表示したい要望が出た時点で追加すればよいか、最初から
   秋季地区大会スコープと同時に実装してしまうか。

---

## 11. 分割案（1 PR = 1 issue・依存順）

起票はオーナー承認後（本issueでは起票しない）。依存のある順に並べる。括弧内は依存先。

未決だった Q63〜Q66 は 2026-11-28 にオーナー決定で決着（OPEN-QUESTIONS 参照）: 実測は⑨で行う・春に3位決定戦は入れない・形式はまず全県共通（実形式は⑧）・春だけの成績スコープは作らない。

1. **春季大会の骨格**（依存なし）: `TournamentKind.Spring`/`SpringRegional` の追加、`SeasonCalendar`への
   `SpringTournamentStartWeek`定数、`TournamentDate`/`TournamentSchedule`への開幕月日、
   `TournamentKindNames`・`CrowdTurnout`・`TournamentPressure`・`MatchStadium`の switch 拡張
   （§2.3の一覧を実装チェックリストとして使う）。この時点ではまだ大会に入れない（土台だけ）。
2. **県大会フォーマット＋県大会への入場**（①に依存）: 汎用プリセットの `PrefFormat`（knockout）を
   春用に適用する経路、参加確認ダイアログ（§5）、`SpringEntry`（辞退可）。背景シムはまだ全国斉一
   （夏/秋と同じ全県背景）で仮実装し、自校の県大会だけをまず動かすスコープに絞る。
3. **地区大会フォーマット＋県大会からの即座連鎖**（②に依存）: `spring-regional-tournaments.yaml`、
   `SpringRegionalEntry`（`RegionalEntry`と同型）、出場枠判定（`KnockoutPlacement`で順位化）。
4. **裏試合の自地区限定化＋AI校進化からの除外ガード**（③に依存）: §3の絞り込み（自地区の県だけ
   フルシム・他地区は一切回さない）と、**§3.2のYearLedger除外ガード**（最重要・単独でテストを書く）。
5. **シード算出の差し替え**（③④に依存）: `TournamentDraw.Arrange`／`TournamentPreview`の4箇所
   （§4）を「春季大会の順位があれば順位、無ければ`School.Strength`」の二択関数に差し替え。
   夏のシード生成が春の実装前に書いたコメント（`TournamentDraw.cs:55-56`）を更新する。
6. **成績・記録・注目度・名声の接続確認**（④⑤に依存）: §6のうち要改修（`MatchSuccessStageMult`への
   `SpringRegional`ケース追加）を実施。他は汎用実装の確認テストを追加するだけ（大半は改修不要の見込み）。
7. **セーブ版数（v54）＋移行**（②〜⑥すべてに依存。実装の最後に積む）: §7。
8. **（backlog・優先度低）47都道府県の春季大会フォーマット実データ収録**: 秋のPhase4ロールアウトと
   同じ形の並行タスク。地区割り・出場枠の真正性を上げる。
9. **（backlog・優先度低）出場/辞退の釣り合い検証＋必要なら調整**: §9.3/§10-1。④⑤がHeavyで安定した後、
   複数年キャリアシムで実測し、案A/B/Cのどれにするかをオーナーへ報告。

②③④の3つは実装順としては③より先に④の一部（絞り込みの仕組み自体）を用意しておいたほうがテストしやすい
可能性があるが、依存関係としては③（地区大会の存在）が無いと④（地区大会ぶんの絞り込み）のテストが書けない
ため上記の順序を推奨する。

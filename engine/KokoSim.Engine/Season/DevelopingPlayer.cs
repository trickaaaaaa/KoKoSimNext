using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 習得済み変化球（設計書02 §2.2）。ランクは PitchRank レベルからのオフセットで持ち、
/// 育成で PitchRank が伸びれば全球種が底上げされる（投影時に絶対値へ変換）。
/// </summary>
public sealed record LearnedPitch(PitchType Type, int PowerOffset, int SharpnessOffset);

/// <summary>
/// 育成中の選手（可変）。表示層能力の現在値・経験値・才能上限（隠し）・成長タイプを持つ。
/// 試合出場時は Players.Player へ投影する（Phase 2 資産の再利用）。
/// </summary>
public sealed class DevelopingPlayer
{
    private static readonly int Count = AbilityKinds.All.Length;
    private static readonly int PositionCount = Enum.GetValues(typeof(FieldPosition)).Length;

    private readonly int[] _level = new int[Count];
    private readonly double[] _exp = new double[Count];
    private readonly int[] _cap = new int[Count];
    // 守備位置適性（設計書01 §1.1）: 全9ポジション個別。練習・実戦で上昇。既定は中庸50。
    private readonly int[] _aptitude = Enumerable.Repeat(50, PositionCount).ToArray();
    private readonly double[] _aptitudeExp = new double[PositionCount];
    private readonly int[] _aptitudeCap = Enumerable.Repeat(99, PositionCount).ToArray();

    /// <summary>
    /// ロスター内で一意・安定な選手ID（成績集計の帰属キー）。ロスター構築時に一度だけ連番付与し以後不変
    /// （乱数なし・決定論、不変条件#2）。投影時に Player.SourceId → ボックススコア SourceId へ伝播し、
    /// 通算/今大会成績へ紐づく。既定0＝未割当（相手校の生成選手など集計対象外）。
    /// </summary>
    public int Id { get; set; }

    public string Name { get; init; } = "部員";
    public int Grade { get; set; } = 1;             // 学年 1〜3

    // 投手役はここには無い（#366b）。選手は「自分は投手だ」という状態を持たない＝必要な場所が
    // 守備位置適性から導出する（PitcherRoleResolver.LeansPitcher）。投手は配置であって属性ではない。

    /// <summary>
    /// 球質タイプ（試合層の <see cref="PitcherAttributes.Archetype"/> からの逆投影・issue #1302）。投手総合が型の上乗せを
    /// 差し引くための材料で、<see cref="Nation.ScoutedTeamProfile"/> が集計用に一時的に載せる。セーブには載せない
    /// （自校の選手は生成時に型を捨てている）。null＝型なし＝差し引かない。
    /// </summary>
    public PitcherArchetype? Archetype { get; init; }

    /// <summary>
    /// 投打の利き（設計書01 §1.1c）。既定は右。生成時は投手らしさ・能力が出てから判定するので set 可
    /// （issue #1475・<see cref="HandednessModel"/>）。生成後に書き換えるのは生成器だけ。
    /// </summary>
    public Handedness Throws { get; set; } = Handedness.Right;
    public Handedness Bats { get; set; } = Handedness.Right;

    /// <summary>
    /// 対左投手の経験（加重打席数。設計書02 §2.3「左のプレミア」・issue #1072）。入学時は個人差あり
    /// （<see cref="Season.ProspectGenerator.Intake"/> が中学での経験を独立Forkで抽選）。公式戦/練習試合で
    /// 左投手と対戦した打席ぶん、重み付き（公式戦1.0／練習試合0.5）で増える（<see cref="LeftExperienceGrowthModel"/>）。
    /// 試合投影時に <see cref="Players.Player.LeftExperiencePa"/> へそのまま写す。既定0＝未生成（旧セーブ相当）。
    /// </summary>
    public double LeftExperiencePa { get; set; }

    /// <summary>
    /// 習得済み変化球（ストレートは必修のため含めない, 設計書02 §2.2）。
    /// 投手1〜3球種／野手0〜1。習得イベントで後から増える。
    /// </summary>
    public List<LearnedPitch> LearnedPitches { get; } = new();

    /// <summary>
    /// 基本タイプ（設計書02 §5.2・issue #866）。生成でまず引く1つのタイプ。スカウトが開示するのもこれ
    /// （スカウト画面は分野を出さない・2026-11-10 監督決定）。走力・精神力・捕手リード・走塁系
    /// （Speed/Bunt/Steal/Baserunning・Mental・Lead）など3分野に属さない能力は常にこれで伸びる時期が決まる。
    /// </summary>
    public GrowthType GrowthType { get; init; } = GrowthType.Standard;

    /// <summary>投球分野の成長タイプ（null=基本タイプ(<see cref="GrowthType"/>)に従う＝旧セーブ相当）。</summary>
    public GrowthType? PitchingGrowthType { get; init; }
    /// <summary>打撃分野の成長タイプ（スイング・選球眼・弾道。null=基本タイプに従う）。</summary>
    public GrowthType? BattingGrowthType { get; init; }
    /// <summary>守備分野の成長タイプ（null=基本タイプに従う）。</summary>
    public GrowthType? DefenseGrowthType { get; init; }

    /// <summary>
    /// 能力 <paramref name="k"/> が属する分野の成長タイプ（設計書02 §5.2・issue #866）。
    /// 分野別の型（上記3つ）が未設定（null＝旧セーブ）なら基本タイプへ落ちる＝旧セーブは全分野
    /// 「基本タイプ1つ」として読める（挙動不変）。走力・走塁系（分野外）は常に基本タイプ。
    /// </summary>
    public GrowthType GrowthTypeFor(AbilityKind k) => GrowthTypeOf(AbilityKinds.DomainOf(k));

    private GrowthType GrowthTypeOf(GrowthDomain d) => d switch
    {
        GrowthDomain.Pitching => PitchingGrowthType ?? GrowthType,
        GrowthDomain.Batting => BattingGrowthType ?? GrowthType,
        GrowthDomain.Defense => DefenseGrowthType ?? GrowthType,
        _ => GrowthType, // 走塁（Speed/Bunt/Steal/Baserunning）: 分野に属さない
    };

    /// <summary>気づき（InsightModel）の分野名文字列から成長タイプを引く（"pitching"/"batting"/"defense"、他は基本タイプ）。</summary>
    public GrowthType GrowthTypeForField(string field) => field switch
    {
        "pitching" => GrowthTypeOf(GrowthDomain.Pitching),
        "batting" => GrowthTypeOf(GrowthDomain.Batting),
        "defense" => GrowthTypeOf(GrowthDomain.Defense),
        _ => GrowthType,
    };

    /// <summary>
    /// 逸材（隠しフラグ, Issue #114 / OPEN-QUESTIONS Q20）。素質固定能力（足）の才能上限圧縮・
    /// trainability 減衰を免除される稀な外れ値＝「規格外の俊足」等。生成時に低確率でロールする
    /// （既定 false・出現率は data 駆動の RosterCoefficients.ProdigyProb）。
    /// </summary>
    public bool IsProdigy { get; init; }

    /// <summary>
    /// 怪物（隠しフラグ・issue #1468・Q20）。自校は新入生にたまに混じるか、スカウトで口説いた中学生。
    /// AI校から転任で引き継いだ選手は AI 側の値を写す。None＝怪物でない（旧セーブ相当）。
    /// </summary>
    public PhenomType Phenom { get; set; } = PhenomType.None;

    /// <summary>精神力（設計書02 §3）。実戦出場でのみ成長する系（練習では伸びない, MatchGrowthModel）。</summary>
    public int Mental { get; set; } = AbilityScale.AverageDisplay;
    /// <summary>精神力の実戦経験値（設計書02 §5.3a, Q8）。必要expは能力値と同じ曲線。</summary>
    public double MentalExp { get; set; }
    /// <summary>精神力の隠し上限（Q8。生成時に才能ギャップ＋Late上振れで決まる）。</summary>
    public int MentalCap { get; set; } = 99;

    /// <summary>
    /// 捕手リード（配球の質, 設計書01 §2①）。天性（野球脳＝Mental相関）で素地が決まり、
    /// 捕手として実戦出場すると伸びる系（練習では伸びない, MatchGrowthModel）。捕手のみ意味を持つ。
    /// </summary>
    public int Lead { get; set; } = AbilityScale.AverageDisplay;
    /// <summary>捕手リードの実戦経験値（設計書02 §5.3a, Q8）。</summary>
    public double LeadExp { get; set; }
    /// <summary>捕手リードの隠し上限（Q8。野球脳と相関＋Late上振れ＝「名捕手の天井」）。</summary>
    public int LeadCap { get; set; } = 99;

    /// <summary>
    /// 投手センス（隠し能力・設計書01 §1.3・issue #1472）。生成時に球速の土台として引いた値をそのまま持つ
    /// （<see cref="ProspectGenerator"/>）。効きは3つ: 配球の質・盗塁の読みと牽制（試合へ投影）／投手系能力の伸びやすさ
    /// （<see cref="DevelopmentModel"/>・打撃の才能とは独立）。練習では伸びない。表示Lv（物差し −Δ）で持つ。
    /// 選手画面の能力表には出さず、見極め（<see cref="InsightModel"/>）で推定が <see cref="PitcherSenseReading"/> に残る。
    /// </summary>
    public int PitcherSense { get; set; } = AbilityScale.AverageDisplay;

    /// <summary>
    /// 見極めで分かった投手センスの見立て（ランク表記のための表示Lv。読み違えもあり得る）。null＝まだ分からない。
    /// </summary>
    public int? PitcherSenseReading { get; set; }

    /// <summary>
    /// 投手の素質の見出しの段（issue #1523・<see cref="PitcherTalentModel"/>）。<see cref="PitcherTalentStage.Discovered"/>（見出された）に
    /// なると投手系の伸びの倍率が上がる（<see cref="TrainingCoefficients.PitcherAwakenedGrowthFactor"/>）。選手詳細には常時は出さない。
    /// </summary>
    public PitcherTalentStage PitcherTalent { get; set; }

    /// <summary>統率傾向（性格の一部, CHANGELOG 22b）。主将選定の軸（設計書09 §8）。</summary>
    public int Leadership { get; init; } = 50;

    /// <summary>性格②素直さ（設計書23 §1.1。1〜99・50が中立）。個別指導・伝令の落ち着きの効き。</summary>
    public int Receptiveness { get; init; } = 50;

    /// <summary>性格③勤勉さ（設計書23 §1.1）。自主成長（<see cref="Players.PersonalityCoefficients.SelfGrowthFactor"/>）。</summary>
    public int Diligence { get; init; } = 50;

    /// <summary>性格④試合での姿勢（設計書23 §1.1。高い＝献身／低い＝目立ちたがり）。犠打・チャンス・伝令役の得意場面。</summary>
    public int Selflessness { get; init; } = 50;

    /// <summary>
    /// 自主成長の隠れた個人差（隠しパラメータ・画面に出さない。OPEN-QUESTIONS Q47）。勤勉さの効きに足す平均0の値で、
    /// 同じ性格タイプの中の自主成長のばらつきを旧 PersonalityFactor 相当（σ≈0.06）に保つ。既定0＝勤勉さの効きだけ。
    /// </summary>
    public double SelfGrowthJitter { get; init; }

    /// <summary>
    /// 性格タイプ（設計書23 §1.3）。4軸（①は <see cref="Leadership"/>）から導出するラベルで、別には保存しない。
    /// ②③④がすべて50（既定）なら Normal＝無補正。
    /// </summary>
    public Players.Personality Personality
        => Players.Personalities.Classify(Leadership, Receptiveness, Diligence, Selflessness);

    /// <summary>
    /// 見極めで開示された性格の軸（設計書23 §1.5・Q37）。軸ごとに「高い側と見えた／低い側と見えた／未開示」を持つ
    /// （誤認もあり得る＝本当の値とずれていてよい）。4軸そろうとタイプ名が判明する（<see cref="Players.PersonalityReading.KnownType"/>）。
    /// </summary>
    public Players.PersonalityReading PersonalityReading { get; set; }

    /// <summary>
    /// 主将か（設計書09 §8）。ロスター上で1名のみ true。年度替わりで3年生が抜けたら選び直す（CaptainSelector）。
    /// 試合編成時に Team.Captain へ投影され、在場×統率力でプレッシャー負補正を緩和する。
    /// </summary>
    public bool IsCaptain { get; set; }

    /// <summary>
    /// 引退済みか（設計書03 §2・issue #954: 自校の夏の敗退直後に3年生が引退する。統計シムと安全網の固定フックは第18週）。引退してもロスターからは除去せず
    /// このフラグを立てて残す（＝記録は残るが、練習・試合・選手一覧の対象から外れる）。
    /// 除去は年度替わり（4月）の卒業でまとめて行う。フラグ操作は <see cref="RosterLifecycle"/> に集約する。
    /// </summary>
    public bool IsRetired { get; set; }

    /// <summary>
    /// 背番号（設計書06 §3.3b: 1〜20＝ベンチ入り、0＝ベンチ外）。監督がメンバー設定画面で割り当てる。
    /// ロスター内で1〜20は一意（重複させない）。割当・検証は <see cref="UniformNumberAssigner"/> に集約する。
    /// 選抜（RosterTeamBuilder）とは独立に持つ純データで、乱数を含まない（不変条件#2）。
    /// </summary>
    public int UniformNumber { get; set; }

    /// <summary>
    /// 成長推移（設計書06 §3.2・issue #1027）。毎月の頭の全能力レベル。記録は <see cref="AbilityHistory"/> だけが行い、
    /// 自校の部員にしか付かない（AI校の選手は空のまま）。観測だけで乱数・能力には触れない。
    /// </summary>
    public List<AbilitySample> History { get; } = new();

    /// <summary>特殊能力（設計書10）。生成時保有＋後天取得。set 可（習得・気づきイベント）。</summary>
    public Players.SkillSet Skills { get; set; } = Players.SkillSet.Empty;

    // 伸びしろ（隠し・分野別成長効率倍率, 設計書01 §1.1 / 02 §5.1）。現在値と独立。既定1.0で中立。
    // set 可: 伸び悩みイベント（設計書03 §5.5）が恒久的に下げることがある。
    public double PitchingGrowth { get; set; } = 1.0;
    public double BattingGrowth { get; set; } = 1.0;
    public double DefenseGrowth { get; set; } = 1.0;

    /// <summary>練習計画（設計書03 §3.1）。null = お任せ（投手適性からの導出で自動選択）。UIから設定・コピー。</summary>
    public TrainingPlan? Plan { get; set; }

    /// <summary>個別指導3枠に指名中か（設計書06 §3.3・Issue #126）。true の週は主効果expへ分野別指導力
    /// 由来の追加倍率が乗る（DevelopmentModel.TrainWeek/TrainWeekPlan）。既定false=従来一致。</summary>
    public bool IndividualCoaching { get; set; }

    /// <summary>調子の内部連続値（-1〜+1, 設計書02 §3.3）。週次更新。表示・投影は FormModel.Quantize で5段階化。</summary>
    public double ConditionValue { get; set; }

    // --- 怪我（設計書03 §3.5: 段階制・常に可視） ---
    public InjurySeverity Injury { get; set; } = InjurySeverity.None;
    public InjurySite InjurySite { get; set; }
    /// <summary>傷病の種類（表示名は data/injuries.yaml。None=種類なしの旧データ）。</summary>
    public InjuryType InjuryType { get; set; } = InjuryType.None;
    public int InjuryWeeksRemaining { get; set; }
    /// <summary>怪我耐性（隠し, 1〜100。高いほど発生しにくい）。</summary>
    public double InjuryResistance { get; init; } = 50.0;

    // --- 成長イベント状態（設計書03 §5.5） ---
    /// <summary>スランプ残り週（>0 の間は一時的な能力ダウン。立て直せる）。</summary>
    public int SlumpWeeks { get; set; }
    /// <summary>イップス（送球/制球の不調, 恒久寄り。克服イベントで解消）。</summary>
    public bool HasYips { get; set; }
    /// <summary>発症時に下げた能力（克服で同じ能力を戻す。null＝発症の記録なし＝旧セーブ）。</summary>
    public AbilityKind? YipsAbility { get; set; }
    /// <summary>発症時に実際に下がった量（下限1で5未満のこともある）。克服ではこの量だけ戻す。</summary>
    public int YipsDrop { get; set; }

    public int Level(AbilityKind k) => _level[(int)k];
    public int Cap(AbilityKind k) => _cap[(int)k];
    public double Exp(AbilityKind k) => _exp[(int)k];

    public void SetLevel(AbilityKind k, int value) => _level[(int)k] = value;
    public void SetCap(AbilityKind k, int value) => _cap[(int)k] = value;
    public void AddExp(AbilityKind k, double value) => _exp[(int)k] += value;
    public void ConsumeExp(AbilityKind k, double value) => _exp[(int)k] -= value;
    public void IncrementLevel(AbilityKind k) => _level[(int)k]++;
    /// <summary>弾道(LaunchTendency)専用の下げ向き成長（issue #1166）が使う。他の能力は呼ばない。</summary>
    public void DecrementLevel(AbilityKind k) => _level[(int)k]--;

    public int Aptitude(FieldPosition pos) => _aptitude[(int)pos];
    public void SetAptitude(FieldPosition pos, int value) => _aptitude[(int)pos] = value;

    // 守備位置適性の育成（練習でポジション別に上昇, 設計書03 §3.1）。レベル制と同じ必要exp曲線を使う。
    public double AptitudeExp(FieldPosition pos) => _aptitudeExp[(int)pos];
    public int AptitudeCap(FieldPosition pos) => _aptitudeCap[(int)pos];
    public void SetAptitudeCap(FieldPosition pos, int value) => _aptitudeCap[(int)pos] = value;
    public void AddAptitudeExp(FieldPosition pos, double value) => _aptitudeExp[(int)pos] += value;
    public void ConsumeAptitudeExp(FieldPosition pos, double value) => _aptitudeExp[(int)pos] -= value;
    public void IncrementAptitude(FieldPosition pos) => _aptitude[(int)pos]++;

    /// <summary>能力が属する分野の伸びしろ倍率（設計書02 §5.1: 経験値式に乗算）。</summary>
    public double GrowthMultiplier(AbilityKind k) => GrowthOf(AbilityKinds.DomainOf(k));

    /// <summary>分野の伸びしろ倍率。走塁は打撃分野の伸びしろに紐づく（2Bで細分化可）。</summary>
    public double GrowthOf(GrowthDomain d) => d switch
    {
        GrowthDomain.Pitching => PitchingGrowth,
        GrowthDomain.Defense => DefenseGrowth,
        _ => BattingGrowth,
    };

    // 育成曲線の観測＝強さに使う「優劣が意味を持つ中核能力」。
    // 選球眼は練習メニューに伸長経路が無い型属性のため平均から除く（OPEN-QUESTIONS Q4）。
    // 弾道は issue #1166 で専用メニューから少し動けるようになったが、ゴロ型〜フライ型のタイプ軸で
    // 優劣軸ではない（design-01 §1.2）ため、伸長経路の有無に関わらずそもそも「強さ」の平均には含めない。
    private static readonly AbilityKind[] BatterCore =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.Speed,
        AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.Catching,
    };

    /// <summary>
    /// 中核能力の平均レベル。育成曲線の観測に使う。どの能力群を中核とみなすかは
    /// 守備位置適性からの導出で決める（#366b: 選手が役割を覚えていない）。
    /// </summary>
    public double AverageLevel() => AverageLevel(0);

    /// <summary>
    /// 中核能力の平均レベル（各能力に <paramref name="delta"/> を足してから平均）。チーム総合は今の物差し
    /// （表示Lv＋Δ・issue #1080）で測るので Δ を渡す。並べ替えだけの用途は 0 のままで同じ並び。
    /// </summary>
    public double AverageLevel(int delta)
    {
        var kinds = PitcherRoleResolver.LeansPitcher(this) ? AbilityKinds.Pitching : BatterCore;
        var sum = 0.0;
        foreach (var k in kinds) sum += _level[(int)k] + delta;
        return sum / kinds.Length;
    }

    /// <summary>
    /// 可変状態の完全複製（Issue #223）。非破壊ドライラン（練習プレビュー等）用に、レベル/経験値/
    /// 才能上限・守備適性＋適性exp・精神力/リード系・習得球種・隠し属性を丸ごと複製する。
    /// 乱数は使わない（決定論、不変条件#2）。複製後にクローンへ加えた変更は元の選手に影響しない。
    /// </summary>
    public DevelopingPlayer Clone()
    {
        var clone = new DevelopingPlayer
        {
            Id = Id,
            Name = Name,
            Grade = Grade,
            Throws = Throws,
            Bats = Bats,
            LeftExperiencePa = LeftExperiencePa,
            Archetype = Archetype,
            GrowthType = GrowthType,
            PitchingGrowthType = PitchingGrowthType,
            BattingGrowthType = BattingGrowthType,
            DefenseGrowthType = DefenseGrowthType,
            IsProdigy = IsProdigy,
            Phenom = Phenom,
            Mental = Mental,
            MentalExp = MentalExp,
            MentalCap = MentalCap,
            Lead = Lead,
            LeadExp = LeadExp,
            LeadCap = LeadCap,
            PitcherSense = PitcherSense,
            PitcherSenseReading = PitcherSenseReading,
            PitcherTalent = PitcherTalent,
            Leadership = Leadership,
            Receptiveness = Receptiveness,
            Diligence = Diligence,
            Selflessness = Selflessness,
            SelfGrowthJitter = SelfGrowthJitter,
            PersonalityReading = PersonalityReading,
            IsCaptain = IsCaptain,
            IsRetired = IsRetired,
            UniformNumber = UniformNumber,
            Skills = Skills,
            PitchingGrowth = PitchingGrowth,
            BattingGrowth = BattingGrowth,
            DefenseGrowth = DefenseGrowth,
            Plan = Plan,
            IndividualCoaching = IndividualCoaching,
            ConditionValue = ConditionValue,
            Injury = Injury,
            InjurySite = InjurySite,
            InjuryType = InjuryType,
            InjuryWeeksRemaining = InjuryWeeksRemaining,
            InjuryResistance = InjuryResistance,
            SlumpWeeks = SlumpWeeks,
            HasYips = HasYips,
            YipsAbility = YipsAbility,
            YipsDrop = YipsDrop,
        };
        // 投手役は適性からの導出なので複製しない（#377）。適性は下でコピーするため結果は一致する。
        for (var i = 0; i < Count; i++)
        {
            clone._level[i] = _level[i];
            clone._exp[i] = _exp[i];
            clone._cap[i] = _cap[i];
        }
        for (var i = 0; i < PositionCount; i++)
        {
            clone._aptitude[i] = _aptitude[i];
            clone._aptitudeExp[i] = _aptitudeExp[i];
            clone._aptitudeCap[i] = _aptitudeCap[i];
        }
        clone.LearnedPitches.AddRange(LearnedPitches);
        clone.History.AddRange(History);   // 点は不変 record なので浅い複製で足りる

        return clone;
    }
}

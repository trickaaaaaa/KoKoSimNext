using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>新入生生成の係数（設計書04 §2.1・設計書01 §1.1b-c）。名声→総合力→凸凹配分。</summary>
public sealed record RosterCoefficients
{
    public double IntakeMean { get; init; } = 10.0;
    public double IntakeSd { get; init; } = 1.5;
    public int IntakeMin { get; init; } = 8;
    public int IntakeMax { get; init; } = 13;
    /// <summary>
    /// 旧: 生成時に投手を決め打つ確率（Issue #174 で廃止）。投手役は投手適性からの創発に置換したため
    /// 生成では未使用。互換のため残置（他コードが参照する場合のドキュメント値）。
    /// </summary>
    public double PitcherShare { get; init; } = 0.30;

    // --- 投手適性→投手能力の創発（Issue #174: 生成時決め打ち廃止） ---
    /// <summary>
    /// 投手寄せ判定のバイアス（Issue #174）。投手適性に加算してから最良守備位置適性と比較する。
    /// 投手は守備8ポジの最大値（＝より多くの候補の max）と競うため、無バイアスだと創発投手率が
    /// 実プレイに必要な水準より低くなる。この加算で投手供給率を狙い値へ寄せる（帯校正で決める）。
    /// </summary>
    public double PitcherAptitudeBias { get; init; } = 8.0;
    /// <summary>
    /// 投手適性→投手能力の連続化スパン（Issue #174）。(投手適性＋bias − 最良守備適性) をこの幅で
    /// ロジスティック squash して投手寄せ重み w∈[0,1] を作る。小さいほど二極化（≒従来のON/OFF）、
    /// 大きいほどなだらか。投手能力の中心（制球/キレ/投手センス）は offCenter↔total を w で補間する。
    /// </summary>
    public double PitcherAptitudeWeightSpan { get; init; } = 6.0;

    // --- 総合力（名声由来）。既定は一般校相当。名声接続は SeasonEngine 側で talentCenter を渡す（後続） ---
    /// <summary>総合力の既定中心値（＝専門能力の平均目標, 一般校）。名声で上下する。</summary>
    public double TalentCenterDefault { get; init; } = 32.0;
    public double TalentSd { get; init; } = 8.0;
    public double TalentMin { get; init; } = 10.0;
    public double TalentMax { get; init; } = 90.0;

    // --- 凸凹配分（設計書04 §2.1: 総ポイントをランダム重みで配分・偏り自体もばらつく） ---
    /// <summary>配分の偏り強度σ。大きいほど内訳が凸凹（金太郎飴回避）。偏り自体もばらつかせる。</summary>
    public double ConcentrationMean { get; init; } = 0.35;
    public double ConcentrationSd { get; init; } = 0.15;
    /// <summary>専門能力の下限フロア（「できない」でなく「相対的に劣る」に収める）。</summary>
    public int SpecialtyFloor { get; init; } = 14;
    /// <summary>専門外能力の中心係数（総合力×これ）＋低フロア。パワプロ型特化を禁止。</summary>
    public double OffSpecialtyFactor { get; init; } = 0.55;
    public int OffSpecialtyFloor { get; init; } = 8;

    // --- 運動能力ベースライン（走力・地肩・スタミナ, 名声独立, 設計書01 §1.1b-2） ---
    public double PhysicalMean { get; init; } = 45.0;
    public double PhysicalSd { get; init; } = 12.0;

    /// <summary>
    /// 精神力・統率傾向の分布（設計書02 §3 / 09 §8。才能・身体と独立に振る）。
    /// 実戦成長ループ接続（Q8・2026-07-20）に伴い、精神力は「新入生は未熟（中心46）→3年間の実戦で開花」へ
    /// 初期分布を低めに振り直した（設計書02 §5.3a の時間アーク）。
    /// </summary>
    public double MentalMean { get; init; } = 46.0;
    public double MentalSd { get; init; } = 12.0;
    public double LeadershipMean { get; init; } = 50.0;
    public double LeadershipSd { get; init; } = 15.0;

    /// <summary>
    /// 捕手リード（設計書01 §2①）。天性＝野球脳(Mental)相関で素地が決まる。
    /// 実戦成長ループ接続（Q8・2026-07-20）に伴い中心を46へ引き下げ（「未熟→捕手出場で伸びる」の時間アーク）。
    /// </summary>
    public double LeadMean { get; init; } = 46.0;
    public double LeadSd { get; init; } = 8.0;
    public double LeadMentalCorr { get; init; } = 0.30;   // 野球脳との相関（天性の効き）
    /// <summary>リードcapの野球脳相関（Q8(c): 名捕手の天井は野球脳で決まる）。(Mental−50)×これ を cap に加算。</summary>
    public double LeadCapMentalCorr { get; init; } = 0.15;

    // --- 投打の利き（設計書01 §1.1c） ---
    public double ThrowLeftProb { get; init; } = 0.12;
    public double SwitchProb { get; init; } = 0.03;
    public double BatLeftGivenRightThrow { get; init; } = 0.40;
    public double BatLeftGivenLeftThrow { get; init; } = 0.98;

    // --- 投手球速（地肩＋投手センス, 設計書01 §1.1b-3 / 02 §1.2）。level層で連動、km/h校正は後続 ---
    public double PitcherSenseBonus { get; init; } = 10.0;      // 投手は投手センスが上振れ
    public double NonPitcherSenseFactor { get; init; } = 0.45;  // 野手の投手センスは低い
    public double VelocityArmWeight { get; init; } = 0.5;       // 球速level = arm×w1 + sense×w2
    public double VelocitySenseWeight { get; init; } = 0.5;

    // --- 伸びしろ（分野別・凸凹, 設計書01 §1.1 / 02 §5.1）。成長効率倍率、現在値と独立 ---
    public double GrowthMean { get; init; } = 1.0;
    public double GrowthSd { get; init; } = 0.22;
    public double GrowthMin { get; init; } = 0.5;
    public double GrowthMax { get; init; } = 1.6;
    public double LateGrowthBonus { get; init; } = 0.15;       // 晩成の伸びしろ上方補正

    // --- 才能上限（凸凹配分, 現在値＋gap） ---
    public double CapGapMean { get; init; } = 26.0;
    public double CapGapSd { get; init; } = 10.0;
    public double LateCapBonus { get; init; } = 6.0;

    /// <summary>
    /// 足(Speed) の才能上限gap圧縮係数（Issue #114）。素質固定能力は「伸びる速さ」だけでなく「天井」も近い
    /// ＝両方きつく。gap（mean/sd）にこれを乗じる。既定1.0で従来と一致（能力別 gap 係数の第一号）。
    /// 逸材（IsProdigy）はこの圧縮を免除し規格外の俊足になりうる（<see cref="CapGapFactor"/> で分岐）。
    /// </summary>
    public double SpeedCapGapFactor { get; init; } = 1.0;

    /// <summary>
    /// 逸材（隠しフラグ, Issue #114 / Q20）の生成時出現率。素質固定能力の cap 圧縮・trainability 減衰を免除する。
    /// 既定0.0＝逸材なし（従来と完全一致）。出現率は data 駆動。
    /// </summary>
    public double ProdigyProb { get; init; } = 0.0;

    /// <summary>能力別の才能上限gap圧縮係数（Issue #114）。素質固定能力（足）のみ&lt;1.0、他は1.0。</summary>
    public double CapGapFactor(AbilityKind k) => k == AbilityKind.Speed ? SpeedCapGapFactor : 1.0;

    // --- 隠し属性「投手経歴」（設計書01 §1.1b）: 野手の変化球上振れの素 ---
    public double PitcherBackgroundProb { get; init; } = 0.06;

    // --- 変化球レパートリー（設計書02 §2.2）: 投手1〜3球種／野手0〜1（経歴持ちは上振れ） ---
    public double PitcherSecondPitchProb { get; init; } = 0.75;
    public double PitcherThirdPitchProb { get; init; } = 0.30;
    public double FielderBreakingProb { get; init; } = 0.35;
    public double BackgroundFielderExtraProb { get; init; } = 0.60;
    /// <summary>球種ランクのオフセットσ（PitchRankレベル基準の個体差）。</summary>
    public double PitchOffsetSd { get; init; } = 8.0;

    // --- 守備位置適性の初期分布（設計書01 §1.1 / 03 §3.1）。本職は事前に決めず創発させる ---
    // 各選手の「守備地力(base)」＋系統(投/捕/内/外)ごとの独立な向き不向き＋ポジ個体差。
    // 結果として本職が定まる（全ポジ器用も捕手専門も個性として自然発生）。
    /// <summary>守備地力の全体水準の中心。高い個体ほど全ポジ器用になりうる。</summary>
    public double AptitudeBaseMean { get; init; } = 50.0;
    public double AptitudeBaseSd { get; init; } = 14.0;
    /// <summary>系統（投/捕/内野/外野）ごとの向き不向きσ。独立に振る＝本職を事前に決めない。</summary>
    public double AptitudeGroupAffinitySd { get; init; } = 12.0;
    /// <summary>各ポジ個別の個体差σ。</summary>
    public double AptitudePositionNoiseSd { get; init; } = 6.0;

    // 旧フィールド（後方互換・他コードから参照）。生成は上記モデルに移行。
    public double InitLevelMean { get; init; } = 32.0;
    public double InitLevelSd { get; init; } = 8.0;

    /// <summary>球質タイプ（本格派/技巧派/軟投派）の出現割合と配分オフセット。自校・AI校で共有する。</summary>
    public PitcherArchetypeCoefficients Archetypes { get; init; } = new();

    /// <summary>打順編成＋DH使用判断（issue #54, 設計書11 §4）。自校・AI校で共有する。</summary>
    public LineupCoefficients Lineup { get; init; } = new();
}

/// <summary>
/// 新入生（1年生）を生成する。乱数は注入（決定論）。
/// 2段階生成（設計書01 §1.1b）: ①運動能力ベースライン（名声独立）→ ②総合力を専門能力へ凸凹配分。
/// </summary>
public static class ProspectGenerator
{
    // 専門（打撃・守備技術）: 総合力から凸凹配分。地肩(ArmStrength)・走力(Speed)・スタミナは含めない（身体系＝独立）。
    private static readonly AbilityKind[] BattingSkill =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline,
        AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ThrowAccuracy,
        AbilityKind.Bunt, AbilityKind.Steal, AbilityKind.Baserunning,
    };
    // 投手専門（球速を除く）: コントロール・球種ランク。
    private static readonly AbilityKind[] PitchingSkill =
    {
        AbilityKind.Control, AbilityKind.PitchRank,
    };
    // 運動能力ベースライン（名声独立, 設計書01 §1.1b-2）。
    private static readonly AbilityKind[] Physical =
    {
        AbilityKind.Speed, AbilityKind.ArmStrength, AbilityKind.Stamina,
    };

    public static IReadOnlyList<DevelopingPlayer> Intake(int year, RosterCoefficients c, IRandomSource rng,
        PlayerNameVocab? nameVocab = null, double? talentCenter = null, SkillCoefficients? skills = null,
        PersonalityCoefficients? personalities = null, IEnumerable<string>? existingNames = null,
        FormCoefficients? form = null)
    {
        var vocab = nameVocab ?? new PlayerNameVocab();
        // 同一チーム内で下の名前が被らないよう、既存部員の名前も含めて持ち回る（苗字の重複はOK）。
        var usedGiven = new HashSet<string>();
        if (existingNames != null)
            foreach (var n in existingNames) usedGiven.Add(GivenPart(n));
        var skillCoeff = skills ?? new SkillCoefficients();
        var personalityCoeff = personalities ?? new PersonalityCoefficients();
        var formCoeff = form ?? new FormCoefficients();
        var center = talentCenter ?? c.TalentCenterDefault;
        var count = (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(c.IntakeMean, c.IntakeSd)), c.IntakeMin, c.IntakeMax);
        var list = new List<DevelopingPlayer>(count);
        for (var i = 0; i < count; i++)
            list.Add(Create(year, i, center, c, rng, vocab, skillCoeff, personalityCoeff, formCoeff, usedGiven));
        return list;
    }

    /// <summary>「苗字　名前」から下の名前だけを取り出す（区切りが無ければ全体を名前とみなす）。</summary>
    private static string GivenPart(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "";
        var i = fullName.LastIndexOf('　');
        return i >= 0 ? fullName.Substring(i + 1) : fullName;
    }

    private static DevelopingPlayer Create(int year, int index, double talentCenter,
        RosterCoefficients c, IRandomSource rng, PlayerNameVocab vocab, SkillCoefficients skillCoeff,
        PersonalityCoefficients personalityCoeff, FormCoefficients formCoeff, ISet<string> usedGiven)
    {
        var growth = SampleGrowthType(rng);

        // --- 投打の利き（条件付き, 設計書01 §1.1c） ---
        var throws = rng.NextDouble() < c.ThrowLeftProb ? Handedness.Left : Handedness.Right;
        Handedness bats;
        if (rng.NextDouble() < c.SwitchProb) bats = Handedness.Switch;
        else if (throws == Handedness.Left) bats = rng.NextDouble() < c.BatLeftGivenLeftThrow ? Handedness.Left : Handedness.Right;
        else bats = rng.NextDouble() < c.BatLeftGivenRightThrow ? Handedness.Left : Handedness.Right;

        // --- 伸びしろ（分野別・凸凹, 現在値と独立） ---
        double GrowthRoll() => MathUtil.Clamp(
            rng.NextGaussian(c.GrowthMean, c.GrowthSd) + (growth == GrowthType.Late ? c.LateGrowthBonus : 0),
            c.GrowthMin, c.GrowthMax);
        var pitchingGrowth = GrowthRoll();
        var battingGrowth = GrowthRoll();
        var defenseGrowth = GrowthRoll();

        // 精神系（設計書02 §3 / 09 §8）: 独立ストリーム(Fork)で抽選し、既存の能力ロール列を乱さない。
        var mentalRng = rng.Fork(MentalStreamId(year, index));
        var mental = (int)MathUtil.Clamp(Math.Round(mentalRng.NextGaussian(c.MentalMean, c.MentalSd)), 1, 99);
        var leadership = (int)MathUtil.Clamp(Math.Round(mentalRng.NextGaussian(c.LeadershipMean, c.LeadershipSd)), 1, 99);
        // 捕手リード（設計書01 §2①）: 野球脳(Mental)相関＋個体差。
        // mentalRng の末尾で引くことで既存の mental/leadership 値と主ロール列を1ビットも変えない。
        var lead = (int)MathUtil.Clamp(
            Math.Round(c.LeadMean + (mental - 50) * c.LeadMentalCorr + mentalRng.NextGaussian(0, c.LeadSd)), 1, 99);

        // 実戦成長の隠し上限（Q8・2026-07-20）: 能力capと同じ「現在値＋gap＋Late上振れ」の流儀。
        // リードcapはさらに野球脳と相関（Q8(c)）＝「名捕手の天井」を個体で表現。mentalRng 末尾追加のため
        // 既存の mental/leadership/lead 値は不変（決定論の増分互換）。
        var mentalCap = (int)MathUtil.Clamp(
            Math.Round(mental + mentalRng.NextGaussian(c.CapGapMean, c.CapGapSd)
                + (growth == GrowthType.Late ? c.LateCapBonus : 0)), mental + 2, 99);
        var leadCap = (int)MathUtil.Clamp(
            Math.Round(lead + mentalRng.NextGaussian(c.CapGapMean, c.CapGapSd)
                + (growth == GrowthType.Late ? c.LateCapBonus : 0)
                + (mental - 50) * c.LeadCapMentalCorr), lead + 2, 99);

        // 性格タイプ（設計書01 §1.1, CHANGELOG 22b）: 専用フォークストリームで抽選（能力・精神ロール列を1ビットも乱さない）。
        // タイプ先決め: 抽選したタイプが①統率傾向の生成平均偏りと③自主成長(PersonalityFactor)の中心を与える。
        var personality = personalityCoeff.Sample(rng.Fork(PersonalityStreamId(year, index)));
        var profile = personalityCoeff.Profile(personality);
        leadership = (int)MathUtil.Clamp(leadership + profile.LeadershipMeanOffset, 1, 99);

        // 逸材（隠しフラグ, Issue #114 / Q20）: 専用Forkストリームで低確率ロール＝既存の能力ロール列を1ビットも
        // 乱さない（不変条件#2）。ProdigyProb=0（既定）なら常に false。SetWithCap（足のcap圧縮免除）と
        // ApplyExp（trainability減衰免除）が p.IsProdigy を参照する。
        var isProdigy = rng.Fork(ProdigyStreamId(year, index)).NextDouble() < c.ProdigyProb;

        var p = new DevelopingPlayer
        {
            // 氏名は独立ストリーム(Fork)で抽選し、能力ロール列と分離（決定論・名前テスト非破壊）。
            // 重複回避のリロールも同じ Fork ストリーム内で完結する＝主RNGの消費列は不変。
            Name = PlayerNameGenerator.Generate(vocab, rng.Fork(NameStreamId(year, index)), usedGiven),
            Grade = 1,
            GrowthType = growth,
            IsProdigy = isProdigy,
            Throws = throws,
            Bats = bats,
            PitchingGrowth = pitchingGrowth,
            BattingGrowth = battingGrowth,
            DefenseGrowth = defenseGrowth,
            // ③勤勉さ: タイプ中心＋個体ジッタ。主ストリームの NextGaussian 消費は従来と同数＝能力ロール列不変。
            PersonalityFactor = MathUtil.Clamp(
                profile.SelfGrowthFactor + rng.NextGaussian(0, personalityCoeff.FactorJitterSd),
                personalityCoeff.FactorMin, personalityCoeff.FactorMax),
            Personality = personality,
            Mental = mental,
            MentalCap = mentalCap,
            Leadership = leadership,
            Lead = lead,
            LeadCap = leadCap,
            // 怪我耐性（隠し, 設計書01 §1.1）。身体系と同様に才能と独立。
            InjuryResistance = MathUtil.Clamp(rng.NextGaussian(50, 15), 10, 90),
            // 調子の初期値（設計書02 §3.3, issue #50）: 週次AR(1)の定常分布から抽選。
            // 独立ストリームで抽選し既存の能力ロール列を1ビットも変えない（決定論・分布テスト非破壊）。
            ConditionValue = Players.FormModel.SampleInitialCondition(
                rng.Fork(ConditionStreamId(year, index)), formCoeff),
        };

        // 守備位置適性（設計書01 §1.1）: 本職は事前に決めず、地力＋系統の向き不向き＋ポジ個体差から創発させる。
        // 投手適性もここで全選手に振る（Issue #174: 投手も守備位置と同列の創発）。独立ストリーム(Fork)。
        AssignAptitudes(p, c, rng.Fork(AptitudeStreamId(year, index)));

        // 投手役は投手適性から創発（旧 PitcherShare コイン投げ廃止, Issue #174）。
        // 役割判定は供給バイアス込み（実プレイに必要な投手率へ寄せる）で決め、明示ロールとして持たせる。
        // 一方 w（能力の連続補間の重み）は素の適性差から作る＝明確な野手は w≈0 で打撃が total を保つ
        // （バイアスを w に混ぜると全野手の打撃が下がり帯が大きくずれるため分離）。
        var (naturalPitcher, w) = ResolvePitcherRole(p, c);
        p.IsPitcher = naturalPitcher;

        // 隠し「投手経歴」は野手のみ（適性確定後に判定, 設計書01 §1.1b）。主ストリームで1ドロー。
        p.HasPitcherBackground = !naturalPitcher && rng.NextDouble() < c.PitcherBackgroundProb;
        // スキル（設計書10）: 独立ストリームで抽選し能力ロール列を乱さない。投手判定は創発ロール。
        p.Skills = SkillGenerator.Generate(naturalPitcher, leadership, rng.Fork(SkillStreamId(year, index)), skillCoeff);

        // --- Stage 1: 運動能力ベースライン（名声独立） ---
        foreach (var k in Physical)
        {
            var level = (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(c.PhysicalMean, c.PhysicalSd)), 10, 92);
            SetWithCap(p, k, level, c, growth, rng);
        }

        // 弾道は型属性（練習で伸びない）。中庸に振る。
        {
            var lt = (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(50, 15)), 1, 99);
            SetWithCap(p, AbilityKind.LaunchTendency, lt, c, growth, rng);
        }

        // --- Stage 2: 総合力→専門能力へ凸凹配分（投手寄せは w で連続化, Issue #174） ---
        var total = MathUtil.Clamp(rng.NextGaussian(talentCenter, c.TalentSd), c.TalentMin, c.TalentMax);
        var concentration = MathUtil.Clamp(rng.NextGaussian(c.ConcentrationMean, c.ConcentrationSd), 0.05, 1.0);
        var offCenter = total * c.OffSpecialtyFactor;

        // 投手センス（隠し, 球速levelの土台）: 野手水準(total×係数) ↔ 投手上振れ(total+bonus) を w で補間。
        // 二段分岐を廃し、投手適性に対して連続に振る（旧: isPitcher で total+bonus か total×0.45 の二択）。
        var senseMean = Lerp(total * c.NonPitcherSenseFactor, total + c.PitcherSenseBonus, w);
        var pitcherSense = MathUtil.Clamp(senseMean + rng.NextGaussian(0, 6), 5, 95);

        // 専門配分の中心・フロアも w で連続補間（w=1で投手が本業＝total／w=0で野手が本業＝total）。
        // 投手系(制球/キレ)は offCenter↔total、打撃・守備系は total↔offCenter を逆向きに補間する。
        var pitchingCenter = Lerp(offCenter, total, w);
        var battingCenter = Lerp(total, offCenter, w);
        var pitchingFloor = (int)Math.Round(Lerp(c.OffSpecialtyFloor, c.SpecialtyFloor, w));
        var battingFloor = (int)Math.Round(Lerp(c.SpecialtyFloor, c.OffSpecialtyFloor, w));
        Distribute(p, PitchingSkill, pitchingCenter, concentration, pitchingFloor, c, growth, rng);
        Distribute(p, BattingSkill, battingCenter, concentration, battingFloor, c, growth, rng);

        // 投手球速level = 地肩 ＋ 投手センス（設計書01 §1.1b-3）。地肩は Stage1 で確定済み。
        var arm = p.Level(AbilityKind.ArmStrength);
        var veloLevel = (int)MathUtil.Clamp(
            Math.Round(arm * c.VelocityArmWeight + pitcherSense * c.VelocitySenseWeight), 1, 100);

        // 球質タイプ（設計書01 §1.1b「技巧派投手」）: 投手にのみ付与＝創発の投手判定で分岐。
        // 投手総合はほぼ変えず球速・制球・スタミナ・キレの配分だけ振り替える。専用Forkストリームで抽選。
        if (naturalPitcher)
        {
            var archetype = PitcherArchetypes.Sample(
                rng.Fork(ArchetypeStreamId(year, index)), c.Archetypes);
            var (dv, dc, ds, dr) = PitcherArchetypes.Offsets(archetype, c.Archetypes);
            veloLevel = Lv(veloLevel + dv);
            SetWithCap(p, AbilityKind.Control, Lv(p.Level(AbilityKind.Control) + dc), c, growth, rng);
            SetWithCap(p, AbilityKind.Stamina, Lv(p.Level(AbilityKind.Stamina) + ds), c, growth, rng);
            SetWithCap(p, AbilityKind.PitchRank, Lv(p.Level(AbilityKind.PitchRank) + dr), c, growth, rng);
        }
        SetWithCap(p, AbilityKind.Velocity, veloLevel, c, growth, rng);

        // 変化球レパートリー（設計書02 §2.2）。ストレートは必修（含めない）。
        // 投手: 1〜3球種／野手: 0〜1（投手経歴持ちは1〜2に上振れ）。判定は創発ロール。
        LearnPitches(p, naturalPitcher, c, rng);

        return p;
    }

    private static readonly PitchType[] BreakingTypes =
    {
        PitchType.TwoSeam, PitchType.Cutter, PitchType.Slider, PitchType.Curve,
        PitchType.Fork, PitchType.Changeup, PitchType.Shuuto, PitchType.Sinker,
    };

    private static void LearnPitches(DevelopingPlayer p, bool isPitcher, RosterCoefficients c, IRandomSource rng)
    {
        int count;
        if (isPitcher)
        {
            count = 1;
            if (rng.NextDouble() < c.PitcherSecondPitchProb) count++;
            if (rng.NextDouble() < c.PitcherThirdPitchProb) count++;
        }
        else if (p.HasPitcherBackground)
        {
            count = 1 + (rng.NextDouble() < c.BackgroundFielderExtraProb ? 1 : 0);
        }
        else
        {
            count = rng.NextDouble() < c.FielderBreakingProb ? 1 : 0;
        }

        // 重複しない球種を選ぶ（順序依存を避けるため先頭から確率抽選でなくインデックス抽選）。
        var pool = new List<PitchType>(BreakingTypes);
        for (var i = 0; i < count && pool.Count > 0; i++)
        {
            var idx = rng.NextInt(0, pool.Count);
            var type = pool[idx];
            pool.RemoveAt(idx);
            var powerOff = (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(0, c.PitchOffsetSd)), -20, 20);
            var sharpOff = (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(0, c.PitchOffsetSd)), -20, 20);
            p.LearnedPitches.Add(new LearnedPitch(type, powerOff, sharpOff));
        }
    }

    /// <summary>総合力 center を kinds へ凸凹配分（重み=exp(N(0,σ))を平均1正規化 → mean(level)≈center）。</summary>
    private static void Distribute(DevelopingPlayer p, AbilityKind[] kinds, double center, double concentration,
        int floor, RosterCoefficients c, GrowthType growth, IRandomSource rng)
    {
        var n = kinds.Length;
        var weights = new double[n];
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            weights[i] = Math.Exp(rng.NextGaussian(0, concentration));
            sum += weights[i];
        }
        var wmean = sum / n;
        for (var i = 0; i < n; i++)
        {
            var raw = center * weights[i] / wmean;
            var level = (int)MathUtil.Clamp(Math.Round(raw), floor, 90);
            SetWithCap(p, kinds[i], level, c, growth, rng);
        }
    }

    // 全9守備位置（適性付与の反復用, enum順）。
    private static readonly FieldPosition[] AllPositions =
    {
        FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    /// <summary>
    /// 守備位置適性を創発的に付与（設計書01 §1.1）。本職は事前に決めない。
    /// 地力(base)＋系統(投/捕/内/外)ごとの独立な向き不向き＋ポジ個体差の和。
    /// 全ポジ器用・捕手専門・器用貧乏など、結果としてのプロフィールがそのまま個性になる。
    /// ロール本体は <see cref="AptitudeRoller"/> に共有（Issue #177: AI校と同じロールを使う）。
    /// </summary>
    private static void AssignAptitudes(DevelopingPlayer p, RosterCoefficients c, IRandomSource rng)
    {
        var apt = AptitudeRoller.Roll(c, rng);
        foreach (var pos in AllPositions)
            p.SetAptitude(pos, apt[(int)pos]);
    }

    /// <summary>現在値を設定し、才能上限＝現在値＋gap（凸凹・晩成補正）を振る。</summary>
    private static void SetWithCap(DevelopingPlayer p, AbilityKind k, int level,
        RosterCoefficients c, GrowthType growth, IRandomSource rng)
    {
        // 能力別 cap gap 圧縮（Issue #114）: 素質固定能力（足）は天井も近い＝両方きつく。逸材（IsProdigy）は免除。
        // gap の mean/sd を同率で縮める。NextGaussian は mean/sd に依らず同数の乱数を消費するため、
        // 係数1.0（既定・非逸材の他能力）では従来と1ビットも変わらない（不変条件#2）。
        var gapFactor = c.CapGapFactor(k);
        if (p.IsProdigy) gapFactor = Math.Max(1.0, gapFactor);
        var gap = rng.NextGaussian(c.CapGapMean * gapFactor, c.CapGapSd * gapFactor)
                  + (growth == GrowthType.Late ? c.LateCapBonus : 0);
        var cap = (int)MathUtil.Clamp(Math.Round(level + gap), level + 2, 99);
        p.SetLevel(k, level);
        p.SetCap(k, cap);
    }

    /// <summary>氏名抽選用の独立ストリームID（学年×連番で一意, 学年間の名前相関を避ける）。</summary>
    private static ulong NameStreamId(int year, int index) => (ulong)(year * 1000 + index + 1);

    /// <summary>精神系の独立ストリームID（氏名ストリームと衝突しない上位ビットを立てる）。</summary>
    private static ulong MentalStreamId(int year, int index) => 0x3E37_0000UL ^ (ulong)(year * 1000 + index + 1);

    /// <summary>スキル抽選の独立ストリームID（他ストリームと別の上位ビット）。</summary>
    private static ulong SkillStreamId(int year, int index) => 0x5C11_0000UL ^ (ulong)(year * 1000 + index + 1);

    /// <summary>守備位置適性の独立ストリームID（他ストリームと別の上位ビット）。</summary>
    private static ulong AptitudeStreamId(int year, int index) => 0x7A19_0000UL ^ (ulong)(year * 1000 + index + 1);

    /// <summary>性格抽選の独立ストリームID（他ストリームと別の上位ビット）。</summary>
    private static ulong PersonalityStreamId(int year, int index) => 0x9D2B_0000UL ^ (ulong)(year * 1000 + index + 1);

    /// <summary>球質タイプ抽選の独立ストリーム（能力ロール列を乱さない）。</summary>
    private static ulong ArchetypeStreamId(int year, int index) => 0x7B15_0000UL ^ (ulong)(year * 1000 + index + 1);

    /// <summary>調子の初期値抽選の独立ストリームID（他ストリームと別の上位ビット, issue #50）。</summary>
    private static ulong ConditionStreamId(int year, int index) => 0x2F84_0000UL ^ (ulong)(year * 1000 + index + 1);

    /// <summary>逸材フラグ抽選の独立ストリームID（他ストリームと別の上位ビット, Issue #114）。</summary>
    private static ulong ProdigyStreamId(int year, int index) => 0x6C43_0000UL ^ (ulong)(year * 1000 + index + 1);

    /// <summary>球質タイプのオフセットを載せたレベルを能力域へ丸める。</summary>
    private static int Lv(double level) => (int)MathUtil.Clamp(Math.Round(level), 1, 100);

    /// <summary>線形補間（Issue #174: 投手寄せ重み w による能力中心の連続補間）。</summary>
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// 投手役（創発）と投手寄せ重み w∈[0,1] を返す（Issue #174）。
    /// 役割: 投手適性＋供給バイアス が最良守備位置適性を上回れば投手（供給率を狙い値へ寄せる）。
    /// w: 素の適性差 (投手適性 − 最良守備適性) をスパンでロジスティック squash（バイアス非混入）。
    ///    明確な野手は w≈0 で打撃が total を保ち、明確な投手は w≈1 で投手能力が total になる。
    /// </summary>
    private static (bool NaturalPitcher, double Weight) ResolvePitcherRole(DevelopingPlayer p, RosterCoefficients c)
    {
        var aptPitcher = p.Aptitude(FieldPosition.Pitcher);
        var bestField = 0;
        foreach (var pos in AllPositions)
            if (pos != FieldPosition.Pitcher) bestField = Math.Max(bestField, p.Aptitude(pos));
        var naturalPitcher = aptPitcher + c.PitcherAptitudeBias > bestField;
        var w = 1.0 / (1.0 + Math.Exp(-(aptPitcher - bestField) / c.PitcherAptitudeWeightSpan));
        return (naturalPitcher, w);
    }

    private static GrowthType SampleGrowthType(IRandomSource rng)
    {
        var r = rng.NextDouble();
        if (r < 0.30) return GrowthType.Early;
        if (r < 0.70) return GrowthType.Standard;
        return GrowthType.Late;
    }
}

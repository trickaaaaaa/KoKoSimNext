using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>新入生生成の係数（設計書04 §2.1・設計書01 §1.1b-c）。名声→総合力→凸凹配分。</summary>
public sealed record RosterCoefficients
{
    /// <summary>
    /// C# 既定値の共有インスタンス。係数を注入できない場所（<see cref="PitcherRoleResolver.LeansPitcher(DevelopingPlayer)"/>
    /// など）から参照する。YAML 側と値が食い違うと導出が二重定義になるので、
    /// data/coefficients.yaml の roster: 節と既定値は必ず揃えること。
    /// </summary>
    public static readonly RosterCoefficients Default = new();

    public double IntakeMean { get; init; } = 10.0;
    public double IntakeSd { get; init; } = 1.5;
    public int IntakeMin { get; init; } = 8;
    public int IntakeMax { get; init; } = 13;
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

    /// <summary>
    /// 「捕手として読める」下限適性（#589・設計書04 §2.3）。判定は
    /// <see cref="CatcherRoleResolver.ReadsAsCatcher(int,RosterCoefficients)"/> に集約する。
    ///
    /// 既定50は <see cref="Match.Fielding.FieldingCoefficients.AptitudeNeutral"/>＝適性補正が付かない点
    /// （×1.0）。これ未満は「捕手に置くと不利補正が付く＝不慣れ」なので、そこを境界に採る。
    /// 供給下限の発動率は実測0.122%（40,000コホート）＝ほぼ発動しない最小限の保険
    /// （design-04 §2.3「硬派さを損なわない最小限」）。
    /// </summary>
    public double CatcherAptitudeFloor { get; init; } = 50.0;

    // --- 総合力（名声由来）。既定は一般校相当。名声接続は SeasonEngine 側で talentCenter を渡す ---
    /// <summary>総合力の既定中心値（＝専門能力の平均目標, 一般校）。名声で上下する。</summary>
    public double TalentCenterDefault { get; init; } = 32.0;
    public double TalentSd { get; init; } = 8.0;
    public double TalentMin { get; init; } = 10.0;
    public double TalentMax { get; init; } = 90.0;

    /// <summary>
    /// 名声(Fame, 0-100)由来のtalentCenter上振れ幅の上限（頭打ち, issue #127・設計書04 §2.1）。
    /// 2026-08-15（案D）に 8→50。旧値は Fame最大でも 32→40（E境界）で、自校の新入生が AI C校（強度60）の
    /// 新入生にも届かず、自校が D 以上へ行く経路が実質閉じていた。新値は名声20→48(E)/40→59(D)/60→67(C)/
    /// 80→72(B)/100→75(B。SD8 なので上位個体は A)。頭打ち自体は残す＝暴走防止の要件は維持し、
    /// 名声は <c>CareerCoefficients.FameDecay</c> で自己制限する。
    /// </summary>
    public double FameTalentCenterSpan { get; init; } = 50.0;

    /// <summary>
    /// 名声由来の上振れが頭打ちへ漸近する速さ（指数飽和の時定数, issue #127）。
    /// 小さいほど低い名声でも早く頭打ちに近づき、大きいほど線形に近い緩やかな伸びになる。
    /// </summary>
    public double FameTalentCenterTau { get; init; } = 50.0;

    /// <summary>
    /// 名声(Fame, 0-100)から新入生の総合力中心(talentCenter)を求める（設計書04 §2.1・issue #127）。
    /// TalentCenterDefault を基準に、名声が高いほど指数飽和カーブ（線形→漸近）で上振れる。
    /// 上限は TalentCenterDefault + FameTalentCenterSpan に頭打ち＝「勝つ→怪物→また勝つ」の暴走防止が必須要件。
    /// Fame=0（または未注入）で TalentCenterDefault と厳密に一致する（不変条件#2: 名声フィードバックoffは従来一致）。
    /// </summary>
    public double TalentCenterFromFame(double fame)
    {
        var f = Math.Max(0.0, fame);
        var shift = FameTalentCenterSpan * (1.0 - Math.Exp(-f / FameTalentCenterTau));
        return TalentCenterDefault + shift;
    }

    // --- ティア→能力の圧縮（issue #578 Step 1・OPEN-QUESTIONS Q24 の確定2） ---

    /// <summary>
    /// 強度→能力中心の圧縮写像のピボット（この強度の学校は圧縮の前後で変わらない）。
    /// <b>ここを平均帯（50）に置くと体験は変わらない</b>: 自校は <see cref="ProspectGenerator"/>（才能中心
    /// <see cref="TalentCenterDefault"/>=32・実力≒35）で生成されて圧縮の対象外なので、ピボット50なら
    /// D(50) の相手は 50 のままで自校との実力差が1ポイントも縮まらない。自校の実力域へ置くのが要件。
    /// </summary>
    public double TierCompressionPivot { get; init; } = 40.0;

    /// <summary>
    /// 強度→能力中心の圧縮率（1.0＝圧縮なし＝従来と1ビット一致）。小さいほどティア間の実力差が縮み、
    /// 格差マッチの得点差が緩む（issue #536 の実測: 実力差10ポイントごとに得点が約0.4倍）。
    /// <b>全国トップ校の絶対能力も同時に下がる</b>ので、値は実測（<c>simulate-tiers</c>）で決める。
    /// </summary>
    public double TierCompressionSlope { get; init; } = 0.55;

    /// <summary>
    /// 圧縮を掛ける上限（この強度より上は圧縮せず、100 で元の尺度へ戻るよう傾きを起こす）。
    /// 100＝膝なし＝全帯域を一様に圧縮する。低い値ほど「低ティアだけ寝かせて上位は保つ」非線形になる
    /// （Q24 の確定3「低ティアだけ持ち上げる（非線形）」）。
    /// <para>一様圧縮（膝なし）は上位校の絶対能力まで削る: pivot40/slope0.55 では強度85の学校の実力が
    /// 76.1→57.6（表示A・中身D）まで落ち、育て切った自校が全国最強になってしまう（実測 2026-08-10）。</para>
    /// </summary>
    public double TierCompressionKnee { get; init; } = 60.0;

    /// <summary>
    /// 学校の強度（<see cref="Nation.School.Strength"/>）を能力中心へ写す（issue #578）。
    /// **表示ランクは元の強度のまま**（Q24 の確定4＝表示は直さない）で、生成だけがこの写像を通る。
    /// 呼ぶのは「ティアが能力中心に変わる入口」だけ＝<see cref="Nation.StrengthTeamFactory.Create"/> と
    /// <c>AiRosterFactory.RecruitCenter</c> の2箇所。個々の能力ロール（<c>Ability()</c>）側では呼ばない
    /// （そこには打撃中心などの派生値も流れるため、二重に圧縮される）。
    /// </summary>
    public double CompressStrength(double strength)
    {
        var knee = TierCompressionKnee;
        var below = TierCompressionPivot + TierCompressionSlope * (Math.Min(strength, knee) - TierCompressionPivot);
        if (strength <= knee || knee >= 100.0) return below;
        // 膝から上は「100 で元の尺度に戻る」傾きで起こす＝上位校の絶対能力を保ったまま
        // 低ティアだけ寝かせる。代わりに中〜上位の勾配は元より急になる（そこは苦情の対象ではない）。
        return below + (100.0 - below) / (100.0 - knee) * (strength - knee);
    }

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
    /// <summary>
    /// 身体系（走力・地肩・スタミナ）中心の名声連動（2026-08-15 案D・設計書01 §1.1b-2 但し書き）。
    /// 身体系中心 = PhysicalMean + (talentCenter − TalentCenterDefault) × これ。0 なら旧来の名声独立。
    /// SD は据え置きなので弱小校の俊足・強肩は残る。0 のままだと名声で入る B/A の新入生が「打撃Aで走力E」になり、
    /// 機動力・肩・球速の天井でチームが C で頭打ちになる。AI校（StrengthTeamFactory）は身体系が強度連動なので、
    /// これで自校だけ天井が低い非対称が消える。既定は data/coefficients.yaml roster.physical_talent_slope と一致。
    /// </summary>
    public double PhysicalTalentSlope { get; init; } = 0.75;

    /// <summary>新入生の総合力中心 <paramref name="talentCenter"/> に対応する身体系中心（<see cref="PhysicalTalentSlope"/>）。</summary>
    public double PhysicalCenterFor(double talentCenter)
        => PhysicalMean + (talentCenter - TalentCenterDefault) * PhysicalTalentSlope;
    public double PhysicalSd { get; init; } = 12.0;

    /// <summary>
    /// スタミナLevel→目安投球数の変換（切片・傾き, 設計書02 §1.1e / issue #408）。既定は
    /// <see cref="Players.PitcherAttributes.StaminaPitchesIntercept"/> / <c>StaminaPitchesPerLevel</c> と
    /// 同一定数を参照する（順変換・逆変換・生成が同じ値で動く一箇所集約）。data/coefficients.yaml の
    /// roster.stamina_pitches_intercept / _per_level と一致（不変条件#4・一致テスト #402）。
    /// </summary>
    public double StaminaPitchesIntercept { get; init; } = Players.PitcherAttributes.StaminaPitchesIntercept;
    public double StaminaPitchesPerLevel { get; init; } = Players.PitcherAttributes.StaminaPitchesPerLevel;

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
    /// 既定は data/coefficients.yaml roster.speed_cap_gap_factor と一致（issue #402）。
    /// </summary>
    public double SpeedCapGapFactor { get; init; } = 0.4;

    /// <summary>
    /// 逸材（隠しフラグ, Issue #114 / Q20）の生成時出現率。素質固定能力の cap 圧縮・trainability 減衰を免除する。
    /// 既定は data/coefficients.yaml roster.prodigy_prob と一致（issue #402で 0.0→0.02＝実ゲームでも逸材が出現）。
    /// </summary>
    public double ProdigyProb { get; init; } = 0.02;

    /// <summary>能力別の才能上限gap圧縮係数（Issue #114）。素質固定能力（足）のみ&lt;1.0、他は1.0。</summary>
    public double CapGapFactor(AbilityKind k) => k == AbilityKind.Speed ? SpeedCapGapFactor : 1.0;

    // --- 隠し属性「投手経歴」（設計書01 §1.1b）: 野手の変化球上振れの素 ---
    public double PitcherBackgroundProb { get; init; } = 0.06;

    // --- 変化球レパートリー（設計書02 §2.2）: 本数は投手適性の連続量(RoleWeight)で決まる ---
    public double PitcherSecondPitchProb { get; init; } = 0.75;
    public double PitcherThirdPitchProb { get; init; } = 0.30;
    public double FielderBreakingProb { get; init; } = 0.35;
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

    /// <param name="fixedCount">
    /// 人数を抽選せず固定する（新設校の初年度＝1年生ちょうどN人・issue #585）。null（既定）なら
    /// 従来どおり <c>IntakeMean/Sd/Min/Max</c> から引く。**指定したときは人数の乱数draw自体を行わない**
    /// ＝以降の乱数列が既定経路とずれるが、これは新設校専用の別経路なので既存の決定論には影響しない。
    /// </param>
    public static IReadOnlyList<DevelopingPlayer> Intake(int year, RosterCoefficients c, IRandomSource rng,
        PlayerNameVocab? nameVocab = null, double? talentCenter = null, SkillCoefficients? skills = null,
        PersonalityCoefficients? personalities = null, IEnumerable<string>? existingNames = null,
        FormCoefficients? form = null, int? fixedCount = null)
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
        var count = fixedCount is { } fc
            ? Math.Max(0, fc)
            : (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(c.IntakeMean, c.IntakeSd)), c.IntakeMin, c.IntakeMax);
        var list = new List<DevelopingPlayer>(count);
        for (var i = 0; i < count; i++)
            list.Add(Create(year, i, center, c, rng, vocab, skillCoeff, personalityCoeff, formCoeff, usedGiven));
        EnsurePitcherSupply(list, c);
        EnsureCatcherSupply(list, c);
        return list;
    }

    /// <summary>
    /// 投手供給の下限（#377）。投手として読める選手が1人も居ないコホートに、投手適性が最も惜しい1人を
    /// 判定境界のすぐ上まで引き上げて必ず1人立たせる。AI校側の
    /// <see cref="Nation.Roster.PersistentRosterCoefficients.PitcherCountFloor"/> と同じ役目。
    ///
    /// 供給バイアスを保っても投手適性者0人のコホートは実測2.12%出るので、判定だけでは
    /// 「投手適性者0人の学校が出ない」を満たせない。乱数を使わない決定論操作（不変条件#2）。
    ///
    /// 能力は既に配分済みなので、底上げされた投手は投手能力が低いままになる＝
    /// 「その年は投手が居ない」という苦作がそのまま絵に出る（能力まで底上げしない）。
    /// </summary>
    private static void EnsurePitcherSupply(List<DevelopingPlayer> cohort, RosterCoefficients c)
    {
        if (cohort.Count == 0) return;
        foreach (var p in cohort)
            if (PitcherRoleResolver.LeansPitcher(p, c)) return;

        // 判定の余裕（投手適性＋バイアス−最良守備適性）が最大＝最も惜しい1人。同点は先頭優先で決定論。
        DevelopingPlayer? best = null;
        var bestMargin = double.NegativeInfinity;
        var bestField = 0;
        foreach (var p in cohort)
        {
            var field = 0;
            foreach (var pos in AllPositions)
                if (pos != FieldPosition.Pitcher) field = Math.Max(field, p.Aptitude(pos));
            var margin = p.Aptitude(FieldPosition.Pitcher) + c.PitcherAptitudeBias - field;
            if (margin <= bestMargin) continue;
            best = p;
            bestMargin = margin;
            bestField = field;
        }
        if (best == null) return;

        // 判定は apt + bias > bestField。ちょうど跨ぐ最小値まで引き上げる（能力には触れない）。
        var lifted = (int)Math.Ceiling(bestField - c.PitcherAptitudeBias) + 1;
        best.SetAptitude(FieldPosition.Pitcher, (int)MathUtil.Clamp(lifted, 1, 99));
    }

    /// <summary>
    /// 捕手供給の下限（#589・設計書04 §2.3「捕手のみ最低保証」）。<see cref="EnsurePitcherSupply"/> と同型で、
    /// 捕手として読める選手（<see cref="CatcherRoleResolver.ReadsAsCatcher(DevelopingPlayer,RosterCoefficients)"/>）が
    /// 1人も居ないコホートに、捕手適性が最も惜しい1人を判定境界ちょうどまで引き上げる。
    ///
    /// 捕手不在は「苦しい年」ではなく守備が組めない詰みなので、投手と同じ扱いで下限を入れる。
    /// 発動率は実測0.122%（40,000コホート・下限50）＝ほぼ発動しない保険であり、
    /// 「硬派さを損なわない最小限」（design-04 §2.3）に収まる。乱数を使わない決定論操作（不変条件#2）。
    ///
    /// 能力（Catching / ThrowAccuracy 等）は底上げしない＝「その年は捕手が居ない」苦しさは絵に残る
    /// （投手版と同じ作法）。
    ///
    /// 候補から**投手役の選手を外す**のが要点。捕手適性を上げると最良守備適性が上がり
    /// <see cref="PitcherRoleResolver.LeansPitcher(DevelopingPlayer,RosterCoefficients)"/> の判定を割り得るので、
    /// 投手を持ち上げると直前の <see cref="EnsurePitcherSupply"/> が保証した投手供給を壊す。
    /// 逆に野手を持ち上げる分には（最良守備適性が上がる方向＝投手側へ倒れない）投手判定は動かない。
    /// </summary>
    internal static void EnsureCatcherSupply(List<DevelopingPlayer> cohort, RosterCoefficients c)
    {
        if (cohort.Count == 0) return;
        foreach (var p in cohort)
            if (CatcherRoleResolver.ReadsAsCatcher(p, c)) return;

        // 捕手適性が最大＝最も惜しい1人。同点は先頭優先で決定論。
        // 全員が投手役という極小コホートでは候補が空になるので、その場合だけ全員から採る。
        DevelopingPlayer? best = null;
        var bestApt = int.MinValue;
        for (var pass = 0; pass < 2 && best == null; pass++)
        {
            foreach (var p in cohort)
            {
                if (pass == 0 && PitcherRoleResolver.LeansPitcher(p, c)) continue;
                var apt = p.Aptitude(FieldPosition.Catcher);
                if (best != null && apt <= bestApt) continue;
                best = p;
                bestApt = apt;
            }
        }
        if (best == null) return;

        best.SetAptitude(FieldPosition.Catcher, CatcherRoleResolver.MinimumAptitude(c));
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
        // 乱さない（不変条件#2）。ProdigyProb=0 なら常に false（既定は 0.02＝YAML と一致, issue #402）。
        // SetWithCap（足のcap圧縮免除）と ApplyExp（trainability減衰免除）が p.IsProdigy を参照する。
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
        // 役割判定は供給バイアス込み（実プレイに必要な投手率へ寄せる）で決める。
        // 一方 w（能力の連続補間の重み）は素の適性差から作る＝明確な野手は w≈0 で打撃が total を保つ
        // （バイアスを w に混ぜると全野手の打撃が下がり帯が大きくずれるため分離）。
        // #377: 判定結果を選手へ書き戻さない（役割は適性からの導出＝同じ式が2箇所に増えない）。
        // w＝能力の配分に使う重み（バイアス無し）、wRole＝「投手としてどれだけ読めるか」（バイアス込み・
        // 判定境界で 0.5）。持ち物は wRole、能力の配分は w と使い分ける（PitcherRoleResolver 参照）。
        var (naturalPitcher, w, wRole) = ResolvePitcherRole(p, c);

        // 「投手経歴」（旧 設計書01 §1.1b）は #366b で概念ごと廃止。野手の変化球上振れは
        // 投手適性からの連続配分（LearnPitches の wRole）に溶けているので、免罪符のフラグは要らない。
        // スキル（設計書10）: 独立ストリームで抽選し能力ロール列を乱さない。プールの出し分けも連続量。
        p.Skills = SkillGenerator.Generate(wRole, leadership, rng.Fork(SkillStreamId(year, index)), skillCoeff);

        // --- Stage 1: 運動能力ベースライン（中心は名声に半連動・SDは独立, §1.1b-2） ---
        var physicalCenter = c.PhysicalCenterFor(talentCenter);
        foreach (var k in Physical)
        {
            var level = (int)MathUtil.Clamp(Math.Round(rng.NextGaussian(physicalCenter, c.PhysicalSd)), 10, 92);
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

        // 球質タイプ（設計書01 §1.1b「技巧派投手」）: #377 で役割分岐を廃し、**全員に抽選して
        // オフセットを wRole で薄める**（投手として読めない選手ほど振れ幅が小さい）。投手総合はほぼ変えず
        // 球速・制球・スタミナ・キレの配分だけ振り替える。専用Forkストリームで抽選＝主RNG非消費。
        // 反映は AdjustLevel（才能上限を引き直さない）＝**役割によって主RNGの消費数が変わらない**。
        {
            var archetype = PitcherArchetypes.Sample(
                rng.Fork(ArchetypeStreamId(year, index)), c.Archetypes);
            var (dv, dc, ds, dr) = PitcherArchetypes.Offsets(archetype, c.Archetypes);
            // オフセットで専門外フロア（「できない」を作らない, Stage2 の pitchingFloor）を割らせない。
            // 旧実装は投手だけが通る道で floor が SpecialtyFloor(14) と高く問題にならなかったが、
            // 全員が通るようになると野手の制球が OffSpecialtyFloor(8) を割る（実測3件/1635）。
            int Adjusted(AbilityKind k, double delta) => Math.Max(pitchingFloor, Lv(p.Level(k) + delta));
            veloLevel = Lv(veloLevel + dv * wRole);
            AdjustLevel(p, AbilityKind.Control, Adjusted(AbilityKind.Control, dc * wRole));
            // スタミナは上限なし（issue #356）＝軟投派等の正オフセットを100で頭打ちにしない。
            AdjustLevel(p, AbilityKind.Stamina,
                Math.Max(pitchingFloor, LvStamina(p.Level(AbilityKind.Stamina) + ds * wRole)));
            AdjustLevel(p, AbilityKind.PitchRank, Adjusted(AbilityKind.PitchRank, dr * wRole));
        }
        SetWithCap(p, AbilityKind.Velocity, veloLevel, c, growth, rng);

        // 球威（Issue #274）: ストレートのホップ成分を担う育成軸。球速練習の主効果に接続。
        // 本流 rng を1ビットも消費しないよう専用 Fork ストリームから引く（CoreTwelve列の後方互換を保つ）。
        // #377: 二択を廃止。もともと真側が w 補間で、w=0 では偽側と一致するので分岐を畳むだけで連続になる。
        {
            var stuffRng = rng.Fork(StuffStreamId(year, index));
            var stuffBase = Lerp(total * c.NonPitcherSenseFactor, veloLevel + c.PitcherSenseBonus * 0.3, w);
            var stuffLevel = (int)MathUtil.Clamp(Math.Round(stuffBase + stuffRng.NextGaussian(0, 8)), 5, 95);
            SetWithCap(p, AbilityKind.Stuff, stuffLevel, c, growth, stuffRng);
        }

        // 変化球レパートリー（設計書02 §2.2）。ストレートは必修（含めない）。
        // 投手: 1〜3球種／野手: 0〜1（投手経歴持ちは1〜2に上振れ）。判定は創発ロール。
        LearnPitches(p, wRole, c, rng);

        return p;
    }

    private static readonly PitchType[] BreakingTypes =
    {
        PitchType.TwoSeam, PitchType.Cutter, PitchType.Slider, PitchType.Curve,
        PitchType.Fork, PitchType.Changeup, PitchType.Shuuto, PitchType.Sinker,
    };

    /// <summary>
    /// 変化球レパートリーの習得（#377: 役割の三分岐を連続量へ）。3回の抽選を**必ず**引くので
    /// 主RNGの消費数が役割に依存しない（旧実装は分岐ごとに消費数が違った）。
    ///
    /// 重みは「投手としてどれだけ読めるか」（<see cref="PitcherRoleResolver.RoleWeight"/>＝バイアス込み・
    /// 判定境界で 0.5）を使う。能力配分の w（バイアス無し）を使うと、判定を通った投手でも重みが
    /// 0.2〜0.3 に沈み、変化球を1本も持たない投手が15%出る（実測）。持ち物は役割寄りの量で決める。
    ///
    /// 1本目は「野手水準（経歴持ちは必ず）↔ 必ず1本」の補間、2本目は「経歴持ちの上振れ↔投手水準」、
    /// 3本目は投手水準に比例。wRole→0 は従来の野手（0〜1本）、wRole→1 は従来の投手（1〜3本）と一致する。
    /// </summary>
    private static void LearnPitches(DevelopingPlayer p, double wRole, RosterCoefficients c, IRandomSource rng)
    {
        var firstProb = Lerp(c.FielderBreakingProb, 1.0, wRole);
        var secondProb = c.PitcherSecondPitchProb * wRole;
        var count = 0;
        if (rng.NextDouble() < firstProb) count++;
        if (rng.NextDouble() < secondProb) count++;
        if (rng.NextDouble() < c.PitcherThirdPitchProb * wRole) count++;

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

    // 全9守備位置（適性付与の反復用, enum順）。DH を含めないため AptitudeRoller の定義を共有する
    // （複製するとどちらかに DH が混ざったとき絵にも例外にも出ずに投手判定だけが壊れる, #377）。
    private static FieldPosition[] AllPositions => AptitudeRoller.AllPositions;

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
    /// <summary>
    /// 既に確定した能力の現在値だけを置き直す（才能上限を引き直さない＝**乱数を消費しない**, #377）。
    /// 球質タイプのオフセット反映に使う。cap は「現在値＋2」を下回らないよう最小限だけ押し上げる
    /// （上限の抽選をやり直すと、役割ごとに主RNGの消費数が変わって決定論が役割依存になる）。
    /// </summary>
    private static void AdjustLevel(DevelopingPlayer p, AbilityKind k, int level)
    {
        p.SetLevel(k, level);
        if (p.Cap(k) < level + 2) p.SetCap(k, level + 2);
    }

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
        // スタミナだけ才能上限の天井（99）を外す（issue #356）＝伸ばした分だけ長く投げられる。
        // 乱数消費列は不変（NextGaussian は上で共通に引き済み）＝クランプ上限の差し替えだけ（不変条件#2）。
        var rounded = Math.Round(level + gap);
        var cap = k == AbilityKind.Stamina
            ? (int)Math.Max(level + 2, rounded)
            : (int)MathUtil.Clamp(rounded, level + 2, 99);
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

    /// <summary>球威抽選の独立ストリームID（CoreTwelve列を1ビットも乱さない, Issue #274）。</summary>
    private static ulong StuffStreamId(int year, int index) => 0x8D52_0000UL ^ (ulong)(year * 1000 + index + 1);

    /// <summary>球質タイプのオフセットを載せたレベルを能力域へ丸める。</summary>
    private static int Lv(double level) => (int)MathUtil.Clamp(Math.Round(level), 1, 100);

    /// <summary>スタミナ専用の丸め（issue #356）＝上限なし（下限1のみ）。他能力の Lv とは別経路。</summary>
    private static int LvStamina(double level) => (int)Math.Max(1, Math.Round(level));

    /// <summary>線形補間（Issue #174: 投手寄せ重み w による能力中心の連続補間）。</summary>
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// 投手役（創発）と投手寄せ重み w∈[0,1] を返す（Issue #174）。
    /// 役割: 投手適性＋供給バイアス が最良守備位置適性を上回れば投手（供給率を狙い値へ寄せる）。
    /// w: 素の適性差 (投手適性 − 最良守備適性) をスパンでロジスティック squash（バイアス非混入）。
    ///    明確な野手は w≈0 で打撃が total を保ち、明確な投手は w≈1 で投手能力が total になる。
    /// </summary>
    private static (bool NaturalPitcher, double Weight, double RoleWeight) ResolvePitcherRole(
        DevelopingPlayer p, RosterCoefficients c)
    {
        var aptPitcher = p.Aptitude(FieldPosition.Pitcher);
        var bestField = 0;
        foreach (var pos in AllPositions)
            if (pos != FieldPosition.Pitcher) bestField = Math.Max(bestField, p.Aptitude(pos));
        // 判定・重みとも式は PitcherRoleResolver にしか無い（#377: 判定のコピーを作らない）。
        return (PitcherRoleResolver.IsNaturalPitcher(aptPitcher, bestField, c),
                PitcherRoleResolver.PitcherWeight(aptPitcher, bestField, c),
                PitcherRoleResolver.RoleWeight(aptPitcher, bestField, c));
    }

    private static GrowthType SampleGrowthType(IRandomSource rng)
    {
        var r = rng.NextDouble();
        if (r < 0.30) return GrowthType.Early;
        if (r < 0.70) return GrowthType.Standard;
        return GrowthType.Late;
    }
}

using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Season;

/// <summary>気づきの話題（設計書03 §5.6）。隠しパラメータの部分開示。</summary>
public enum InsightTopic
{
    RecentGrowth,      // 「最近伸びてきているようだ」（好調＋成長中のサイン）
    HighGrowthDomain,  // 「〜はまだ伸びる余地がありそうだ」（伸びしろ高の示唆）
    LowGrowthDomain,   // 「〜の伸びしろはないかもしれない」（伸びしろ低の示唆）
    GrowthType,        // 「大器晩成かもしれない」（成長タイプの示唆）
    Personality,       // 「〜な性格かもしれない」（性格の軸1つの示唆, 設計書23 §1.5。4軸そろうとタイプ名が判明）
    HiddenSkill,       // 隠しスキルに気づいた（即開示・issue #1471）。HiddenSkill に何のスキルか入る
    PitcherSense,      // 「投手のセンスは〜くらいに見える」（投手センスの見立て・issue #1472）。PitcherSenseReading に見立て
}

/// <summary>
/// 気づき通知（設計書03 §5.6）。断定しない温度で提示し、外れることもある（IsAccurate=false は読み違え）。
/// UI はこのレコードを「〜かもしれない」文言に整形する。IsAccurate は内部検証用（プレイヤーには見せない）。
/// </summary>
public sealed record InsightNotice(DevelopingPlayer Player, InsightTopic Topic, string Domain, bool IsAccurate)
{
    /// <summary>個別指導に指名中の選手の気づき（見極め・issue #702）。UI が「個別指導で見えてきた」と添える。</summary>
    public bool ViaCoaching { get; init; }

    /// <summary>
    /// <see cref="InsightTopic.GrowthType"/> のとき、どの分野についての示唆か（"pitching"/"batting"/"defense"、
    /// 成長タイプの分野別化・issue #866）。<see cref="Domain"/> には示唆された成長タイプの表示名が入る
    /// （この話題では Domain は分野名ではなく型名＝既存の流儀を保つ）。他の話題では未使用（""）。
    /// </summary>
    public string GrowthField { get; init; } = "";

    /// <summary>
    /// <see cref="InsightTopic.Personality"/> のとき、どの軸についての示唆か（設計書23 §1.5・Q37）。
    /// <see cref="Domain"/> には示唆された側の言い方（「素直」「目立ちたがり」等）が入る。他の話題では未使用。
    /// </summary>
    public Players.PersonalityAxis PersonalityAxis { get; init; }

    /// <summary>
    /// この気づきで4軸の読みがそろったとき、読みから導出したタイプ（誤認込み）。そろっていなければ null。
    /// </summary>
    public Players.Personality? RevealedType { get; init; }

    /// <summary>
    /// <see cref="InsightTopic.HiddenSkill"/> のとき、表に出た隠しスキル（issue #1471・オーナー判断 2026-11-23＝即開示）。
    /// 適用済み（<see cref="DevelopingPlayer.Skills"/> の可視側へ移してある）。他の話題では null。
    /// </summary>
    public Players.Skill? HiddenSkill { get; init; }

    /// <summary>
    /// <see cref="InsightTopic.PitcherSense"/> のとき、見立てた投手センス（表示Lv。読み違えなら本当の値からずれている・issue #1472）。
    /// 部員の <see cref="DevelopingPlayer.PitcherSenseReading"/> にも記録済み。他の話題では null。
    /// </summary>
    public int? PitcherSenseReading { get; init; }
}

/// <summary>気づきの係数（設計書03 §5.6）。🟡頻度・精度カーブは検証で調整。</summary>
public sealed record InsightCoefficients
{
    /// <summary>選手1人あたり週次の基礎発生率（育成眼50時）。「たまに気づく」。</summary>
    public double BaseWeeklyProb { get; init; } = 0.006;
    /// <summary>育成眼1あたりの頻度増（高いほど気づきが増える）。</summary>
    public double ProbPerTalentEye { get; init; } = 0.00008;
    /// <summary>精度の基礎値（育成眼0時。低いと読み違える）。</summary>
    public double AccuracyBase { get; init; } = 0.55;
    /// <summary>育成眼1あたりの精度増（上限0.97: 完全な答え合わせにはしない）。</summary>
    public double AccuracyPerTalentEye { get; init; } = 0.004;
    public double AccuracyMax { get; init; } = 0.97;
    /// <summary>
    /// 個別指導の効果2「見極め」（issue #702・設計書03 §3.3）。指名中の選手だけ発生率に掛ける倍率。
    /// 1.0 で無効（指名の有無で差が出ない）。2026-10-31 監督決定 ×5。
    /// </summary>
    public double NominatedProbFactor { get; init; } = 5.0;
    /// <summary>見極め: 指名中の選手だけ精度に足す値（上限は <see cref="AccuracyMax"/> のまま）。監督決定 +0.15。</summary>
    public double NominatedAccuracyAdd { get; init; } = 0.15;
    /// <summary>気づきの話題: 調子の連続値がこの値以上のとき「成長中の兆し」を話題にしうる。</summary>
    public double RecentGrowthConditionMin { get; init; } = 0.2;
    /// <summary>同・上の条件を満たしたときに成長中の兆しを話題にする確率。</summary>
    public double RecentGrowthTopicProb { get; init; } = 0.4;
    /// <summary>性格の兆しを話題にする確率（性格が Normal でない選手のみ）。</summary>
    public double PersonalityTopicProb { get; init; } = 0.30;
    /// <summary>成長タイプの兆しを話題にする確率。</summary>
    public double GrowthTypeTopicProb { get; init; } = 0.25;
    /// <summary>伸びしろの話題で「高い分野」を選ぶ確率（残りは「低い分野」）。</summary>
    public double HighGrowthDomainProb { get; init; } = 0.5;
    /// <summary>
    /// 隠しスキルを持つ選手に正しく気づいたとき、それを話題にする（＝開示する）確率（issue #1471）。
    /// 気配の段は作らず、話題になったら即開示する（オーナー判断 2026-11-23）。読み違えのときは開示しない。
    /// </summary>
    public double HiddenSkillTopicProb { get; init; } = 0.5;
    /// <summary>
    /// 投手センスの見立てを話題にする確率（issue #1472）。投手センスは隠し能力で、選手画面には見極めで分かった
    /// 見立て（最後のもの）だけが出る。
    /// </summary>
    public double PitcherSenseTopicProb { get; init; } = 0.25;
    /// <summary>読み違えたときに見立てがずれる幅（表示Lv。上下どちらかへ）。おおむねランク1〜2つぶん。</summary>
    public int PitcherSenseMisreadOffset { get; init; } = 15;

    // --- 投手の素質の見出し（issue #1523・PitcherTalentModel）。隠しの投手センスと見える投手能力の差で、見立て→外部コーチの2段 ---
    /// <summary>見立ての対象になる投手センスの下限（物理Lv）。</summary>
    public double PitcherTalentSenseMin { get; init; } = 42.0;
    /// <summary>見立ての対象になる「投手センス − 見える投手能力（球速・制球・キレの平均）」の下限（物理Lv）。</summary>
    public double PitcherTalentGapMin { get; init; } = 15.0;
    /// <summary>対象の選手に見立てが出る週次の確率。</summary>
    public double PitcherTalentHintProb { get; init; } = 0.015;
    /// <summary>見立て済みの選手が外部コーチに見出される（覚醒する）週次の確率。</summary>
    public double PitcherTalentDiscoverProb { get; init; } = 0.04;
    /// <summary>見出された（覚醒した）選手の投手系能力の才能上限に足す値（表示Lv・99で止める）。</summary>
    public int PitcherTalentCapBonus { get; init; } = 15;
}

/// <summary>
/// 気づきイベント（設計書03 §5.6）。監督の育成眼を通じて隠しパラメータの兆しを受動的に開示する。
/// 個別指導の「見極め」（能動的な精度向上）と相補。育成眼が高いほど頻度・精度が上がる。
/// </summary>
public static class InsightModel
{
    /// <summary>
    /// 週次の気づき抽選。talentEye=監督の育成眼(1〜100)。状態は性格の読み（DevelopingPlayer.PersonalityReading）・投手センスの見立て（issue #1472）の記録と、気づいた隠しスキルの開示（issue #1471）だけで、能力等は変更しない。
    /// <paramref name="probFactor"/>／<paramref name="accuracyAdd"/> はビデオ・分析機材などの備品由来
    /// （1.0／0.0＝備品なし＝従来一致）。精度の上限は <see cref="InsightCoefficients.AccuracyMax"/> のまま。
    /// </summary>
    public static IReadOnlyList<InsightNotice> Week(
        IReadOnlyList<DevelopingPlayer> roster, double talentEye, IRandomSource rng, InsightCoefficients c,
        double probFactor = 1.0, double accuracyAdd = 0.0)
    {
        var notices = new List<InsightNotice>();
        var prob = (c.BaseWeeklyProb + talentEye * c.ProbPerTalentEye) * probFactor;
        var accuracy = MathUtil.Clamp(
            c.AccuracyBase + talentEye * c.AccuracyPerTalentEye + accuracyAdd, 0.0, c.AccuracyMax);

        // 個別指導の「見極め」（issue #702）: 指名中の選手だけ発生率と精度を上乗せする。
        // 乱数の引き方（1人1回＋当たれば精度と話題）は指名の有無で変えない＝誰も指名していなければ従来と1ビット一致。
        var nominatedProb = prob * c.NominatedProbFactor;
        var nominatedAccuracy = MathUtil.Clamp(accuracy + c.NominatedAccuracyAdd, 0.0, c.AccuracyMax);

        foreach (var p in roster)
        {
            var nominated = p.IndividualCoaching;
            if (!MathUtil.Chance(nominated ? nominatedProb : prob, rng)) continue;

            var accurate = MathUtil.Chance(nominated ? nominatedAccuracy : accuracy, rng);
            notices.Add(Compose(p, accurate, rng, c) with { ViaCoaching = nominated });
        }
        return notices;
    }

    private static InsightNotice Compose(DevelopingPlayer p, bool accurate, IRandomSource rng, InsightCoefficients c)
    {
        // 隠しスキル（issue #1471）: 正しく気づいたときだけ、たまに眠った素質を見抜く＝即開示する（気配の段は作らない）。
        // 乱数は隠しスキルを持つ選手で正しく気づいたときにだけ引く＝持たない選手の話題の抽選列は従来と1ビット一致。
        if (accurate && p.Skills.Hidden.Count > 0 && rng.NextDouble() < c.HiddenSkillTopicProb)
        {
            var hidden = p.Skills.Hidden.OrderBy(s => s).ToList();   // 集合の列挙順に頼らない（決定論）
            var skill = hidden[rng.NextInt(0, hidden.Count)];
            p.Skills = p.Skills.Reveal(skill);
            return new InsightNotice(p, InsightTopic.HiddenSkill, "", accurate) { HiddenSkill = skill };
        }

        // 話題を選ぶ: 成長中の兆し > 性格 > 伸びしろの高低 > 成長タイプ。
        if (p.ConditionValue >= c.RecentGrowthConditionMin && rng.NextDouble() < c.RecentGrowthTopicProb)
        {
            return new InsightNotice(p, InsightTopic.RecentGrowth, "", accurate);
        }

        // 性格の兆し（設計書23 §1.5・Q37）: たまに性格の軸1つに触れる（「素直な子のようだ」）。読み違え時は逆の側に見える。
        // 読みは部員の PersonalityReading に記録し、4軸そろうとタイプ名が判明する（誤認込みの見立て）。
        if (p.Personality != Players.Personality.Normal && rng.NextDouble() < c.PersonalityTopicProb)
        {
            var axis = (Players.PersonalityAxis)rng.NextInt(0, 4);
            var trueHigh = Players.Personalities.High(AxisValue(p, axis));
            var shownHigh = accurate ? trueHigh : !trueHigh;
            p.PersonalityReading = p.PersonalityReading.With(axis, shownHigh);
            return new InsightNotice(p, InsightTopic.Personality,
                Players.Personalities.AxisSideName(axis, shownHigh), accurate)
            {
                PersonalityAxis = axis,
                RevealedType = p.PersonalityReading.KnownType,
            };
        }

        // 投手センスの見立て（issue #1472）: 隠し能力。たまに投手センスを見立てる（新しい見立てで上書き＝見直しもある）。
        // 読み違えると上下どちらかへ PitcherSenseMisreadOffset ずれた見立てになる（選手画面には最後の見立てが出る）。
        if (rng.NextDouble() < c.PitcherSenseTopicProb)
        {
            var shown = p.PitcherSense;
            if (!accurate)
            {
                var off = rng.NextDouble() < 0.5 ? -c.PitcherSenseMisreadOffset : c.PitcherSenseMisreadOffset;
                shown = (int)MathUtil.Clamp(shown + off, Players.AbilityScale.DisplayMin, 99);
            }
            p.PitcherSenseReading = shown;
            return new InsightNotice(p, InsightTopic.PitcherSense, "", accurate) { PitcherSenseReading = shown };
        }

        // 成長タイプの兆し（設計書03 §5.6・issue #614、分野別化・issue #866）。
        // ★ この話題は列挙子だけあって**一度も返されていなかった**（2026-08-14 まで実在しない気づき）。
        //    「大器晩成かもしれない」は隠しパラメータの中でも起用と我慢の判断に直結するので、
        //    伸びしろの高低より前に置く。読み違え時は別のタイプを示唆する。
        //    分野別化で「投手としては晩成型かもしれない」のように分野を名指しする（pitching/batting/defense から一様に選ぶ）。
        if (rng.NextDouble() < c.GrowthTypeTopicProb)
        {
            var field = GrowthFields[rng.NextInt(0, GrowthFields.Length)];
            var trueType = p.GrowthTypeForField(field);
            var shownType = accurate ? trueType : WrongGrowthType(trueType, rng);
            return new InsightNotice(p, InsightTopic.GrowthType, GrowthTypeNames.DisplayName(shownType), accurate)
            { GrowthField = field };
        }

        // 分野別伸びしろの最大/最小を話題にする。読み違え(accurate=false)時は逆の示唆を返す。
        var (highDomain, lowDomain) = GrowthExtremes(p);
        if (rng.NextDouble() < c.HighGrowthDomainProb)
        {
            var domain = accurate ? highDomain : lowDomain;
            return new InsightNotice(p, InsightTopic.HighGrowthDomain, domain, accurate);
        }
        else
        {
            var domain = accurate ? lowDomain : highDomain;
            return new InsightNotice(p, InsightTopic.LowGrowthDomain, domain, accurate);
        }
    }

    /// <summary>成長タイプの話題で名指しする分野（issue #866）。基本タイプは別経路（スカウト開示）があるのでここには含めない。</summary>
    private static readonly string[] GrowthFields = { "pitching", "batting", "defense" };

    /// <summary>読み違え用: 本当の成長タイプと異なるタイプを1つ選ぶ（必ず別タイプになる）。</summary>
    private static GrowthType WrongGrowthType(GrowthType actual, IRandomSource rng)
    {
        var others = GrowthTypeNames.OtherTypes(actual);
        return others[rng.NextInt(0, others.Length)];
    }

    /// <summary>性格の軸の値（①統率は Leadership）。</summary>
    private static int AxisValue(DevelopingPlayer p, Players.PersonalityAxis axis) => axis switch
    {
        Players.PersonalityAxis.Leadership => p.Leadership,
        Players.PersonalityAxis.Receptiveness => p.Receptiveness,
        Players.PersonalityAxis.Diligence => p.Diligence,
        _ => p.Selflessness,
    };

    private static (string High, string Low) GrowthExtremes(DevelopingPlayer p)
    {
        var pairs = new (string Name, double V)[]
        {
            ("pitching", p.PitchingGrowth), ("batting", p.BattingGrowth), ("defense", p.DefenseGrowth),
        };
        var high = pairs[0];
        var low = pairs[0];
        foreach (var x in pairs)
        {
            if (x.V > high.V) high = x;
            if (x.V < low.V) low = x;
        }
        return (high.Name, low.Name);
    }
}

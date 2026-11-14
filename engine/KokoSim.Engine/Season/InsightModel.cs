using System.Collections.Generic;
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
}

/// <summary>
/// 気づきイベント（設計書03 §5.6）。監督の育成眼を通じて隠しパラメータの兆しを受動的に開示する。
/// 個別指導の「見極め」（能動的な精度向上）と相補。育成眼が高いほど頻度・精度が上がる。
/// </summary>
public static class InsightModel
{
    /// <summary>
    /// 週次の気づき抽選。talentEye=監督の育成眼(1〜100)。状態は性格の読み（DevelopingPlayer.PersonalityReading）を記録するだけで、能力等は変更しない。
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
            notices.Add(Compose(p, accurate, rng) with { ViaCoaching = nominated });
        }
        return notices;
    }

    private static InsightNotice Compose(DevelopingPlayer p, bool accurate, IRandomSource rng)
    {
        // 話題を選ぶ: 成長中の兆し > 性格 > 伸びしろの高低 > 成長タイプ。
        if (p.ConditionValue >= 0.2 && rng.NextDouble() < 0.4)
        {
            return new InsightNotice(p, InsightTopic.RecentGrowth, "", accurate);
        }

        // 性格の兆し（設計書23 §1.5・Q37）: たまに性格の軸1つに触れる（「素直な子のようだ」）。読み違え時は逆の側に見える。
        // 読みは部員の PersonalityReading に記録し、4軸そろうとタイプ名が判明する（誤認込みの見立て）。
        if (p.Personality != Players.Personality.Normal && rng.NextDouble() < 0.30)
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

        // 成長タイプの兆し（設計書03 §5.6・issue #614、分野別化・issue #866）。
        // ★ この話題は列挙子だけあって**一度も返されていなかった**（2026-08-14 まで実在しない気づき）。
        //    「大器晩成かもしれない」は隠しパラメータの中でも起用と我慢の判断に直結するので、
        //    伸びしろの高低より前に置く。読み違え時は別のタイプを示唆する。
        //    分野別化で「投手としては晩成型かもしれない」のように分野を名指しする（pitching/batting/defense から一様に選ぶ）。
        if (rng.NextDouble() < 0.25)
        {
            var field = GrowthFields[rng.NextInt(0, GrowthFields.Length)];
            var trueType = p.GrowthTypeForField(field);
            var shownType = accurate ? trueType : WrongGrowthType(trueType, rng);
            return new InsightNotice(p, InsightTopic.GrowthType, GrowthTypeNames.DisplayName(shownType), accurate)
            { GrowthField = field };
        }

        // 分野別伸びしろの最大/最小を話題にする。読み違え(accurate=false)時は逆の示唆を返す。
        var (highDomain, lowDomain) = GrowthExtremes(p);
        if (rng.NextDouble() < 0.5)
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

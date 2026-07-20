using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>怪我の係数（設計書03 §3.5）。🟡発生率・回復週・能力ダウン幅は検証で調整。</summary>
public sealed record InjuryCoefficients
{
    /// <summary>健常時の週次基礎発生率。</summary>
    public double WeeklyBaseProb { get; init; } = 0.004;
    /// <summary>怪我耐性（隠し, 50基準）1あたりの発生率減。</summary>
    public double ResistanceSlope { get; init; } = 0.010;

    /// <summary>発生時の段階分布（軽度/中度/重度）。</summary>
    public double MinorShare { get; init; } = 0.70;
    public double ModerateShare { get; init; } = 0.25;
    // 残りが重度

    /// <summary>段階ごとの回復目安週（この週数で1段階下がる）。</summary>
    public int MinorRecoveryWeeks { get; init; } = 2;
    public int ModerateRecoveryWeeks { get; init; } = 4;
    public int SevereRecoveryWeeks { get; init; } = 8;

    /// <summary>段階ごとの能力一律係数（試合出場時, 物理層へ落ちる前に乗算。設計書03 §3.5接続）。</summary>
    public double MinorPerformanceFactor { get; init; } = 0.95;
    public double ModeratePerformanceFactor { get; init; } = 0.85;
    public double SeverePerformanceFactor { get; init; } = 0.70;

    /// <summary>「押して出場」1試合あたりの悪化確率（基本的には損な選択に設計）。</summary>
    public double PlayThroughWorsenProb { get; init; } = 0.30;
    /// <summary>悪化時の離脱延長週。</summary>
    public int WorsenExtraWeeks { get; init; } = 3;

    /// <summary>医療施設による回復週短縮率（設計書04の備品。未接続時は1.0）。</summary>
    public double MedicalRecoveryFactor { get; init; } = 1.0;
}

/// <summary>
/// 怪我システム（設計書03 §3.5）。段階制・常に可視。
/// 「見抜けるか」ではなく「この状態で使うか」の葛藤に集中させる。
/// 発生・回復は週次（SeasonEngine）、出場ペナルティは投影（RosterTeamBuilder）、
/// 押して出場の悪化は采配層から PlayThrough を呼ぶ。
/// </summary>
public static class InjuryModel
{
    /// <summary>週次の発生判定。怪我していない選手に対して呼ぶ。発生したら選手状態を書き換え true。</summary>
    public static bool WeeklyCheck(DevelopingPlayer p, IRandomSource rng, InjuryCoefficients c,
        Players.SkillCoefficients? skills = null)
    {
        if (p.Injury != InjurySeverity.None) return false;

        var prob = c.WeeklyBaseProb;
        // 怪我耐性（隠し）: 50基準で増減。
        prob *= Math.Max(0.1, 1.0 - (p.InjuryResistance - 50) * c.ResistanceSlope);
        // 体質スキル（設計書10）: 故障しにくい/ケガしやすい。スキルなしなら倍率1.0で従来と一致。
        if (skills is not null)
        {
            if (p.Skills.Has(Players.Skill.Durable)) prob *= skills.DurableInjuryFactor;
            if (p.Skills.Has(Players.Skill.InjuryProne)) prob *= skills.InjuryProneInjuryFactor;
        }

        if (!MathUtil.Chance(MathUtil.Clamp(prob, 0.0, 0.5), rng)) return false;

        var severity = SampleSeverity(rng, c);
        p.Injury = severity;
        p.InjurySite = (InjurySite)rng.NextInt(0, Enum.GetValues(typeof(InjurySite)).Length);
        p.InjuryWeeksRemaining = RecoveryWeeks(severity, c);
        return true;
    }

    /// <summary>週次の回復。休養・時間経過で段階が下がる（Severe→Moderate→Minor→完治）。</summary>
    public static void WeeklyRecover(DevelopingPlayer p, InjuryCoefficients c)
    {
        if (p.Injury == InjurySeverity.None) return;

        p.InjuryWeeksRemaining--;
        if (p.InjuryWeeksRemaining > 0) return;

        p.Injury = p.Injury switch
        {
            InjurySeverity.Severe => InjurySeverity.Moderate,
            InjurySeverity.Moderate => InjurySeverity.Minor,
            _ => InjurySeverity.None,
        };
        p.InjuryWeeksRemaining = p.Injury == InjurySeverity.None
            ? 0
            : RecoveryWeeks(p.Injury, c);
    }

    /// <summary>
    /// 「押して出場」の悪化判定（設計書03 §3.5 ジレンマの核）。怪我した選手を試合に出した後に呼ぶ。
    /// 悪化したら段階が上がり離脱が延びる。戻り値=悪化したか。
    /// </summary>
    public static bool PlayThrough(DevelopingPlayer p, IRandomSource rng, InjuryCoefficients c)
    {
        if (p.Injury == InjurySeverity.None) return false;
        if (!MathUtil.Chance(c.PlayThroughWorsenProb, rng)) return false;

        p.Injury = p.Injury switch
        {
            InjurySeverity.Minor => InjurySeverity.Moderate,
            _ => InjurySeverity.Severe,
        };
        p.InjuryWeeksRemaining = RecoveryWeeks(p.Injury, c) + c.WorsenExtraWeeks;
        return true;
    }

    /// <summary>出場時の能力一律係数（1.0=健常）。物理層へ落ちる前に全能力へ乗算する。</summary>
    public static double PerformanceFactor(InjurySeverity s, InjuryCoefficients c) => s switch
    {
        InjurySeverity.Minor => c.MinorPerformanceFactor,
        InjurySeverity.Moderate => c.ModeratePerformanceFactor,
        InjurySeverity.Severe => c.SeverePerformanceFactor,
        _ => 1.0,
    };

    private static InjurySeverity SampleSeverity(IRandomSource rng, InjuryCoefficients c)
    {
        var r = rng.NextDouble();
        if (r < c.MinorShare) return InjurySeverity.Minor;
        if (r < c.MinorShare + c.ModerateShare) return InjurySeverity.Moderate;
        return InjurySeverity.Severe;
    }

    private static int RecoveryWeeks(InjurySeverity s, InjuryCoefficients c)
    {
        var weeks = s switch
        {
            InjurySeverity.Minor => c.MinorRecoveryWeeks,
            InjurySeverity.Moderate => c.ModerateRecoveryWeeks,
            InjurySeverity.Severe => c.SevereRecoveryWeeks,
            _ => 0,
        };
        return Math.Max(1, (int)Math.Round(weeks * c.MedicalRecoveryFactor));
    }
}

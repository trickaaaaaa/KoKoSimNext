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

    /// <summary>発生時の段階分布（軽度/中度/重度）。傷病カタログに種類別の分布がある場合はそちらが優先。</summary>
    public double MinorShare { get; init; } = 0.70;
    public double ModerateShare { get; init; } = 0.25;
    // 残りが重度

    /// <summary>段階ごとの回復目安週（この週数で1段階下がる）。傷病の種類ごとの倍率が掛かる。</summary>
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
    /// <summary>
    /// 悪化しなくても「押して出場」1試合ごとに延びる全治週（設計書03 §3.5）。
    /// 0 にすると従来の「悪化した時だけ延びる」挙動に戻る。
    /// </summary>
    public int PlayThroughExtraWeeks { get; init; } = 1;

    /// <summary>医療施設による回復週短縮率（設計書04の備品。未接続時は1.0）。</summary>
    public double MedicalRecoveryFactor { get; init; } = 1.0;
}

/// <summary>1件の怪我の内訳（種類・部位・段階・全治週）。抽選結果の受け渡し用の純データ。</summary>
public readonly record struct InjuryDiagnosis(
    InjuryType Type, InjurySite Site, InjurySeverity Severity, int WeeksRemaining);

/// <summary>
/// 怪我システム（設計書03 §3.5）。段階制・常に可視。
/// 「見抜けるか」ではなく「この状態で使うか」の葛藤に集中させる。
/// 発生・回復は週次（SeasonEngine）と試合中の場面駆動（Match.Game.MatchInjuryModel）、
/// 出場ペナルティは投影（RosterTeamBuilder）、押して出場の悪化は試合後（MatchInjuryLedger）。
/// 種類・部位・段階分布・回復倍率は data/injuries.yaml（<see cref="InjuryCatalog"/>）が単一ソース（不変条件#4）。
/// </summary>
public static class InjuryModel
{
    /// <summary>
    /// 怪我耐性（隠し）と体質スキル（設計書10）による発生率倍率。
    /// 週次・試合中の双方でこの1本を使う（補正の掛け方を共通化）。
    /// </summary>
    public static double OccurrenceMultiplier(
        double injuryResistance, SkillSet skills, InjuryCoefficients c, SkillCoefficients? skillCoeff)
    {
        var m = Math.Max(0.1, 1.0 - (injuryResistance - 50) * c.ResistanceSlope);
        if (skillCoeff is not null)
        {
            if (skills.Has(Skill.Durable)) m *= skillCoeff.DurableInjuryFactor;
            if (skills.Has(Skill.InjuryProne)) m *= skillCoeff.InjuryProneInjuryFactor;
        }
        return m;
    }

    /// <summary>週次の発生判定。怪我していない選手に対して呼ぶ。発生したら選手状態を書き換え true。</summary>
    public static bool WeeklyCheck(DevelopingPlayer p, IRandomSource rng, InjuryCoefficients c,
        SkillCoefficients? skills = null, InjuryCatalog? catalog = null)
    {
        if (p.Injury != InjurySeverity.None) return false;

        var prob = c.WeeklyBaseProb * OccurrenceMultiplier(p.InjuryResistance, p.Skills, c, skills);
        if (!MathUtil.Chance(MathUtil.Clamp(prob, 0.0, 0.5), rng)) return false;

        Apply(p, Sample(InjuryScene.Weekly, rng, c, catalog ?? InjuryCatalog.Default));
        return true;
    }

    /// <summary>
    /// 種類→部位→段階の一貫した抽選（設計書03 §3.5）。カタログが空、またはその場面に重みを持つ
    /// 種類が無ければ種類なし（<see cref="InjuryType.None"/>）＋部位一様の従来抽選に落ちる。
    /// </summary>
    public static InjuryDiagnosis Sample(
        InjuryScene scene, IRandomSource rng, InjuryCoefficients c, InjuryCatalog catalog)
    {
        if (catalog.Draw(scene, rng) is not { } draw)
        {
            var all = (InjurySite[])Enum.GetValues(typeof(InjurySite));
            var fallbackSite = all[rng.NextInt(0, all.Length)];
            var fallbackSeverity = InjuryCatalog.SampleSeverity(rng, c.MinorShare, c.ModerateShare);
            return new InjuryDiagnosis(InjuryType.None, fallbackSite, fallbackSeverity,
                RecoveryWeeks(fallbackSeverity, c, 1.0));
        }
        return ToDiagnosis(draw, c);
    }

    /// <summary>カタログの抽選結果に段階別の基準週を掛けて全治週を確定する（試合中発生の試合後適用で使う）。</summary>
    public static InjuryDiagnosis ToDiagnosis(InjuryDraw draw, InjuryCoefficients c)
        => new(draw.Type, draw.Site, draw.Severity,
            RecoveryWeeks(draw.Severity, c, draw.RecoveryWeekFactor));

    /// <summary>診断結果を選手へ書き込む（週次発生・試合中発生の共通口）。</summary>
    public static void Apply(DevelopingPlayer p, InjuryDiagnosis d)
    {
        p.InjuryType = d.Type;
        p.InjurySite = d.Site;
        p.Injury = d.Severity;
        p.InjuryWeeksRemaining = d.WeeksRemaining;
    }

    /// <summary>週次の回復。休養・時間経過で段階が下がる（Severe→Moderate→Minor→完治）。</summary>
    public static void WeeklyRecover(DevelopingPlayer p, InjuryCoefficients c, InjuryCatalog? catalog = null)
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
        if (p.Injury == InjurySeverity.None)
        {
            p.InjuryWeeksRemaining = 0;
            p.InjuryType = InjuryType.None;
            return;
        }
        p.InjuryWeeksRemaining = RecoveryWeeks(p.Injury, c, RecoveryFactorOf(p.InjuryType, catalog));
    }

    /// <summary>
    /// 「押して出場」の判定（設計書03 §3.5 ジレンマの核）。怪我した選手を試合に出した後に呼ぶ。
    /// 悪化したら段階が上がり離脱が大きく延びる。悪化しなくても出場した分だけ全治が延びる
    /// （<see cref="InjuryCoefficients.PlayThroughExtraWeeks"/>）。戻り値=悪化したか。
    /// </summary>
    public static bool PlayThrough(DevelopingPlayer p, IRandomSource rng, InjuryCoefficients c,
        InjuryCatalog? catalog = null)
    {
        if (p.Injury == InjurySeverity.None) return false;

        if (!MathUtil.Chance(c.PlayThroughWorsenProb, rng))
        {
            // 悪化しなくても休めていない分だけ治りが遅れる。
            p.InjuryWeeksRemaining += Math.Max(0, c.PlayThroughExtraWeeks);
            return false;
        }

        p.Injury = p.Injury switch
        {
            InjurySeverity.Minor => InjurySeverity.Moderate,
            _ => InjurySeverity.Severe,
        };
        p.InjuryWeeksRemaining =
            RecoveryWeeks(p.Injury, c, RecoveryFactorOf(p.InjuryType, catalog)) + c.WorsenExtraWeeks;
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

    /// <summary>段階＋種類の倍率から全治週を出す（試合後適用からも使う）。</summary>
    public static int RecoveryWeeks(InjurySeverity s, InjuryCoefficients c, double typeFactor)
    {
        var weeks = s switch
        {
            InjurySeverity.Minor => c.MinorRecoveryWeeks,
            InjurySeverity.Moderate => c.ModerateRecoveryWeeks,
            InjurySeverity.Severe => c.SevereRecoveryWeeks,
            _ => 0,
        };
        if (weeks == 0) return 0;
        return Math.Max(1, (int)Math.Round(weeks * typeFactor * c.MedicalRecoveryFactor));
    }

    private static double RecoveryFactorOf(InjuryType type, InjuryCatalog? catalog)
        => (catalog ?? InjuryCatalog.Default).Find(type)?.RecoveryWeekFactor ?? 1.0;
}

using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation;

/// <summary>
/// チーム総合力の6指標（設計決定 2026-07-18）。各値 0〜100、Overall は6指標の加重平均。
/// 打撃/守備/機動は野手レギュラー、投手はエース偏重、選手層は控えの厚み、精神は主力の平均。
/// </summary>
public sealed record TeamStrength(
    double Batting,
    double Pitching,
    double Defense,
    double Mobility,
    double Depth,
    double Mental,
    double Overall)
{
    /// <summary>総合力からティアを導出（学校のティアと同体系）。</summary>
    public Tier Tier => Tiers.FromStrength(Overall);
}

/// <summary>
/// 6指標の重み（不変条件#4: バランス係数は data/coefficients.yaml の team_strength: 駆動）。
/// たたき台の値。総合の重みは「投手力が王様」、合成のサブ重みは合計1.0で正規化不要にしてある。
/// </summary>
public sealed record TeamStrengthCoefficients
{
    // --- 6指標 → 総合（合計1.0を想定するが、Compute側で総和正規化する） ---
    public double PitchingWeight { get; init; } = 0.28;
    public double BattingWeight { get; init; } = 0.24;
    public double DefenseWeight { get; init; } = 0.18;
    public double DepthWeight { get; init; } = 0.12;
    public double MobilityWeight { get; init; } = 0.10;
    public double MentalWeight { get; init; } = 0.08;

    // --- 打撃合成（1野手, 合計1.0） ---
    public double ContactWeight { get; init; } = 0.35;
    public double PowerWeight { get; init; } = 0.35;
    public double LaunchWeight { get; init; } = 0.15;
    public double DisciplineWeight { get; init; } = 0.15;

    // --- 投手合成（1投手, 合計1.0） ---
    // 球速は投手の格を決める最大要素として重めに置く（速球は打者への物理的な脅威そのもの）。
    // ただし単独では決めない＝制球・キレ・スタミナの合計(0.60)が球速(0.40)を上回る配分にする。
    public double VelocityWeight { get; init; } = 0.40;
    public double ControlWeight { get; init; } = 0.25;
    public double StaminaWeight { get; init; } = 0.15;
    public double PitchRankWeight { get; init; } = 0.20;

    // --- 守備合成（1野手, 合計1.0） ---
    public double FieldingWeight { get; init; } = 0.40;
    public double CatchingWeight { get; init; } = 0.30;
    public double ArmWeight { get; init; } = 0.30;

    // --- 機動力合成（1野手, 合計1.0） ---
    public double SpeedWeight { get; init; } = 0.70;
    public double StealWeight { get; init; } = 0.30;

    // --- 投手力：エース偏重（ace / 2番手 / 3番手以降平均, 合計1.0） ---
    public double AceWeight { get; init; } = 0.50;
    public double SecondPitcherWeight { get; init; } = 0.30;
    public double RestPitcherWeight { get; init; } = 0.20;

    // --- 選手層：控え野手平均＋2枚目以降投手（合計1.0） ---
    public double BenchBatterWeight { get; init; } = 0.60;
    public double BackupPitcherWeight { get; init; } = 0.40;

    /// <summary>野手レギュラー数（打撃・守備・機動の母数）。</summary>
    public int LineupSize { get; init; } = 9;

    /// <summary>選手層で見る控え野手の人数（レギュラーの次から数える）。</summary>
    public int BenchSampleSize { get; init; } = 6;

    // --- 総合のリーグ標準化（③, 2026-07-18）。6指標加重平均(raw)を旧 AverageLevel／AI校Strength 尺度へ写す
    //     線形較正。実測フィット（TeamOverallCalibrationTests, 残差<0.6）。表示・シードとも同一値になる。 ---
    public double OverallScale { get; init; } = 1.037;
    public double OverallOffset { get; init; } = -6.6;
}

/// <summary>
/// 育成選手ロスターから <see cref="TeamStrength"/> を集計する純関数（Unity非依存・決定論）。
/// 自校もAI校（実体化後）も同じ関数を通し、総合ランクの意味とスケールを一致させる。
/// </summary>
public static class TeamStrengthProfile
{
    public static TeamStrength Compute(IReadOnlyList<DevelopingPlayer> roster, TeamStrengthCoefficients c)
    {
        // 野手＝非投手を総合力（AverageLevel）降順。投手＝投手合成の降順。
        var fielders = roster.Where(p => !p.IsPitcher)
            .OrderByDescending(p => p.AverageLevel()).ToList();
        var pitchers = roster.Where(p => p.IsPitcher)
            .OrderByDescending(p => PitcherComposite(p, c)).ToList();

        var regulars = fielders.Take(c.LineupSize).ToList();

        var batting = Avg(regulars, p => BatterComposite(p, c));
        var defense = Avg(regulars, p => DefenseComposite(p, c));
        var mobility = Avg(regulars, p => MobilityComposite(p, c));
        var pitching = PitchingStrength(pitchers, c);
        var depth = DepthStrength(fielders, pitchers, c);
        var mental = MentalStrength(regulars, pitchers);

        var overall = WeightedOverall(batting, pitching, defense, mobility, depth, mental, c);
        return new TeamStrength(batting, pitching, defense, mobility, depth, mental, overall);
    }

    // --- 1選手の合成（サブ重み合計1.0） ---

    private static double BatterComposite(DevelopingPlayer p, TeamStrengthCoefficients c)
        => p.Level(AbilityKind.Contact) * c.ContactWeight
         + p.Level(AbilityKind.Power) * c.PowerWeight
         + p.Level(AbilityKind.LaunchTendency) * c.LaunchWeight
         + p.Level(AbilityKind.Discipline) * c.DisciplineWeight;

    private static double PitcherComposite(DevelopingPlayer p, TeamStrengthCoefficients c)
        => p.Level(AbilityKind.Velocity) * c.VelocityWeight
         + p.Level(AbilityKind.Control) * c.ControlWeight
         + p.Level(AbilityKind.Stamina) * c.StaminaWeight
         + p.Level(AbilityKind.PitchRank) * c.PitchRankWeight;

    private static double DefenseComposite(DevelopingPlayer p, TeamStrengthCoefficients c)
        => p.Level(AbilityKind.Fielding) * c.FieldingWeight
         + p.Level(AbilityKind.Catching) * c.CatchingWeight
         + p.Level(AbilityKind.ArmStrength) * c.ArmWeight;

    private static double MobilityComposite(DevelopingPlayer p, TeamStrengthCoefficients c)
        => p.Level(AbilityKind.Speed) * c.SpeedWeight
         + p.Level(AbilityKind.Steal) * c.StealWeight;

    // --- 指標 ---

    /// <summary>投手力＝エース偏重。2番手・3番手以降が欠ける場合は直近の値で埋める（1枚投手＝エース相当）。</summary>
    private static double PitchingStrength(IReadOnlyList<DevelopingPlayer> pitchers, TeamStrengthCoefficients c)
    {
        if (pitchers.Count == 0) return 0;
        var ace = PitcherComposite(pitchers[0], c);
        var second = pitchers.Count > 1 ? PitcherComposite(pitchers[1], c) : ace;
        var rest = pitchers.Count > 2
            ? pitchers.Skip(2).Average(p => PitcherComposite(p, c))
            : second;
        var wsum = c.AceWeight + c.SecondPitcherWeight + c.RestPitcherWeight;
        return (ace * c.AceWeight + second * c.SecondPitcherWeight + rest * c.RestPitcherWeight) / wsum;
    }

    /// <summary>選手層＝控え野手（レギュラーの次）平均＋2枚目以降投手の合成。厚みが無ければ低くなる。</summary>
    private static double DepthStrength(
        IReadOnlyList<DevelopingPlayer> fielders,
        IReadOnlyList<DevelopingPlayer> pitchers,
        TeamStrengthCoefficients c)
    {
        var bench = fielders.Skip(c.LineupSize).Take(c.BenchSampleSize).ToList();
        var benchAvg = bench.Count > 0 ? bench.Average(p => p.AverageLevel()) : 0.0;

        var backups = pitchers.Skip(1).ToList();
        var backupAvg = backups.Count > 0 ? backups.Average(p => PitcherComposite(p, c)) : 0.0;

        var wsum = c.BenchBatterWeight + c.BackupPitcherWeight;
        return (benchAvg * c.BenchBatterWeight + backupAvg * c.BackupPitcherWeight) / wsum;
    }

    /// <summary>精神力＝主力（レギュラー＋エース）の Mental 平均。試合を担う面々の粘り。</summary>
    private static double MentalStrength(
        IReadOnlyList<DevelopingPlayer> regulars,
        IReadOnlyList<DevelopingPlayer> pitchers)
    {
        var core = new List<DevelopingPlayer>(regulars);
        if (pitchers.Count > 0) core.Add(pitchers[0]);
        return core.Count > 0 ? core.Average(p => p.Mental) : 0.0;
    }

    private static double WeightedOverall(
        double batting, double pitching, double defense, double mobility, double depth, double mental,
        TeamStrengthCoefficients c)
    {
        var wsum = c.BattingWeight + c.PitchingWeight + c.DefenseWeight
                 + c.MobilityWeight + c.DepthWeight + c.MentalWeight;
        if (wsum <= 0) return 0;
        var raw = (batting * c.BattingWeight + pitching * c.PitchingWeight
              + defense * c.DefenseWeight + mobility * c.MobilityWeight
              + depth * c.DepthWeight + mental * c.MentalWeight) / wsum;
        // リーグ標準化（旧 AverageLevel／AI校Strength 尺度へ写す）。
        return Math.Clamp(raw * c.OverallScale + c.OverallOffset, 0.0, 100.0);
    }

    private static double Avg(IReadOnlyList<DevelopingPlayer> players, Func<DevelopingPlayer, double> sel)
        => players.Count > 0 ? players.Average(sel) : 0.0;
}

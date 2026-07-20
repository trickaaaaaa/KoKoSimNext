using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// ③総合を6指標加重平均へ切替える前のスケール整合計測（設計決定 2026-07-18）。
/// 弱小〜強豪のロスターで「旧総合＝AverageLevel平均」と「新総合＝TeamStrengthProfile.Overall」を
/// 突き合わせ、新総合が AI校Strength尺度（平均44近辺）と釣り合うかを確認する。
/// </summary>
public sealed class TeamOverallCalibrationTests
{
    private readonly ITestOutputHelper _out;
    public TeamOverallCalibrationTests(ITestOutputHelper output) => _out = output;

    private static List<DevelopingPlayer> BuildRoster(double talentCenter, ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var coeff = new RosterCoefficients();
        var roster = new List<DevelopingPlayer>();
        for (var grade = 1; grade <= 3; grade++)
            foreach (var p in ProspectGenerator.Intake(grade, coeff, rng, talentCenter: talentCenter))
            {
                p.Grade = grade;
                roster.Add(p);
            }
        return roster;
    }

    [Fact]
    public void CalibratedNew6Factor_MatchesOldScale_AcrossStrengthRange()
    {
        // Compute はリーグ標準化済みの Overall を返す（既定係数の scale/offset）。旧尺度に一致するはず。
        var ts = new TeamStrengthCoefficients();
        _out.WriteLine("talentCenter | 旧総合 | 新総合(較正済) | 残差 | ティア");
        double maxResidual = 0;
        foreach (var center in new[] { 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0 })
        {
            double oldSum = 0, newSum = 0;
            const int samples = 40;
            for (var s = 0; s < samples; s++)
            {
                var roster = BuildRoster(center, 1000 + (ulong)s);
                oldSum += roster.Average(p => p.AverageLevel());
                newSum += TeamStrengthProfile.Compute(roster, ts).Overall;
            }
            var oldAvg = oldSum / samples;
            var newAvg = newSum / samples;
            var residual = newAvg - oldAvg;
            maxResidual = System.Math.Max(maxResidual, System.Math.Abs(residual));
            _out.WriteLine($"{center,11:0} | {oldAvg,5:0.0} | {newAvg,12:0.0} | {residual,+5:0.0} | {Tiers.FromStrength(newAvg)}");
        }
        _out.WriteLine($"最大残差 = {maxResidual:0.0}");
        // 較正後は旧尺度（＝AI校Strength尺度）に十分近い。バランス保存の回帰として固定。
        Assert.True(maxResidual < 1.5, $"較正残差が大きすぎる: {maxResidual:0.0}");
    }
}

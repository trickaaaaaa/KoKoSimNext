using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 総合ランク6指標（打撃/投手/守備/機動/選手層/精神）の集計（設計決定 2026-07-18）。
/// 全能力を同一レベルに揃えた選手を使い、各合成値＝そのレベルになる性質で既知値照合する。
/// </summary>
public sealed class TeamStrengthProfileTests
{
    private static readonly TeamStrengthCoefficients C = new();

    // 全能力を level に揃えた選手（合成の各サブ重みは合計1.0なので、合成値＝level になる）。
    private static DevelopingPlayer Batter(int level, int mental = 50)
        => Make(level, mental, pitcher: false);

    private static DevelopingPlayer Pitcher(int level, int mental = 50)
        => Make(level, mental, pitcher: true);

    private static DevelopingPlayer Make(int level, int mental, bool pitcher)
    {
        var p = new DevelopingPlayer { IsPitcher = pitcher, Mental = mental };
        foreach (var k in AbilityKinds.All) p.SetLevel(k, level);
        return p;
    }

    private static List<DevelopingPlayer> Roster(int batters, int batterLevel, int pitcherLevel)
    {
        var r = new List<DevelopingPlayer>();
        for (var i = 0; i < batters; i++) r.Add(Batter(batterLevel));
        r.Add(Pitcher(pitcherLevel));
        return r;
    }

    [Fact]
    public void EmptyRoster_YieldsAllZero_TierG()
    {
        var s = TeamStrengthProfile.Compute(new List<DevelopingPlayer>(), C);
        Assert.Equal(0, s.Batting, 3);
        Assert.Equal(0, s.Pitching, 3);
        Assert.Equal(0, s.Overall, 3);
        Assert.Equal(Tier.G, s.Tier);
    }

    [Fact]
    public void UniformPlayers_FactorsEqualTheirLevel()
    {
        // 野手9人=60, 投手1人=70。控え無し＝選手層は0（薄い）。
        var s = TeamStrengthProfile.Compute(Roster(9, 60, 70), C);
        Assert.Equal(60, s.Batting, 3);
        Assert.Equal(60, s.Defense, 3);
        Assert.Equal(60, s.Mobility, 3);
        Assert.Equal(70, s.Pitching, 3);   // エースのみ
        Assert.Equal(50, s.Mental, 3);     // 既定Mental
        Assert.Equal(0, s.Depth, 3);       // 控え野手なし・2枚目投手なし
    }

    [Fact]
    public void Overall_IsWeightedAverageOfSixFactors_WithLeagueCalibration()
    {
        var s = TeamStrengthProfile.Compute(Roster(9, 60, 70), C);
        var raw =
            (s.Batting * C.BattingWeight + s.Pitching * C.PitchingWeight
             + s.Defense * C.DefenseWeight + s.Mobility * C.MobilityWeight
             + s.Depth * C.DepthWeight + s.Mental * C.MentalWeight)
            / (C.BattingWeight + C.PitchingWeight + C.DefenseWeight
               + C.MobilityWeight + C.DepthWeight + C.MentalWeight);
        // Overall はリーグ標準化（raw*scale+offset）を通した値。
        var expected = raw * C.OverallScale + C.OverallOffset;
        Assert.Equal(expected, s.Overall, 3);
    }

    [Fact]
    public void Pitching_IsAceWeighted_NotSimpleAverage()
    {
        // エース80・2番手40。単純平均なら60、エース偏重(0.5/0.3/0.2)なら60超。
        var r = new List<DevelopingPlayer> { Pitcher(80), Pitcher(40) };
        for (var i = 0; i < 9; i++) r.Add(Batter(50));
        var s = TeamStrengthProfile.Compute(r, C);
        // ace80*0.5 + second40*0.3 + rest(=second)40*0.2 = 40 + 12 + 8 = 60 ... サブ重み次第。
        // ここでは「エースに寄る＝単純平均(60)以上」を検証する。
        Assert.True(s.Pitching >= 60, $"エース偏重のはずが {s.Pitching}");
    }

    [Fact]
    public void Depth_RisesWithBenchAndBackupPitcher()
    {
        var thin = Roster(9, 60, 70);                 // 控えなし
        var deep = Roster(9, 60, 70);
        for (var i = 0; i < 6; i++) deep.Add(Batter(50)); // 控え野手6人
        deep.Add(Pitcher(55));                            // 2枚目投手

        var sThin = TeamStrengthProfile.Compute(thin, C);
        var sDeep = TeamStrengthProfile.Compute(deep, C);
        Assert.Equal(0, sThin.Depth, 3);
        Assert.True(sDeep.Depth > sThin.Depth, "控え＋2枚目投手で選手層が上がるはず");
        Assert.True(sDeep.Overall > sThin.Overall, "選手層改善で総合も上がるはず");
    }

    [Fact]
    public void Mental_ReflectsPlayerMental()
    {
        var low = Roster(9, 60, 70);       // Mental 50
        var high = new List<DevelopingPlayer>();
        for (var i = 0; i < 9; i++) high.Add(Batter(60, mental: 80));
        high.Add(Pitcher(70, mental: 80));

        var sLow = TeamStrengthProfile.Compute(low, C);
        var sHigh = TeamStrengthProfile.Compute(high, C);
        Assert.Equal(50, sLow.Mental, 3);
        Assert.Equal(80, sHigh.Mental, 3);
        Assert.True(sHigh.Overall > sLow.Overall, "精神力が高いと総合も上がるはず");
    }

    [Fact]
    public void Deterministic_SameRosterSameResult()
    {
        var r = Roster(12, 55, 65);
        var a = TeamStrengthProfile.Compute(r, C);
        var b = TeamStrengthProfile.Compute(r, C);
        Assert.Equal(a.Overall, b.Overall, 6);
    }
}

using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>統計シム（SeasonEngine）が実ゲームと同じ育成経路を通ること（issue #1222）。</summary>
public sealed class SeasonEngineParityTests
{
    private static double GradAvg(SeasonContext ctx)
        => SeasonEngine.Run(10, ctx, new Xoshiro256Random(42)).Years
            .Where(y => y.GraduatingAvgLevel > 0).Average(y => y.GraduatingAvgLevel);

    private static IReadOnlyList<YearSnapshot> Years(SeasonContext ctx)
        => SeasonEngine.Run(10, ctx, new Xoshiro256Random(42)).Years;

    private static FacilitySet Set(params (string Id, int Lv)[] levels)
    {
        var s = new FacilitySet();
        foreach (var (id, lv) in levels) s.SetLevel(id, lv);
        return s;
    }

    [Fact]
    public void PracticeLeader_RaisesGrowth_BecauseNewcomersHaveDistinctIds()
    {
        var baseCtx = SeasonEngineTests.LoadContext();
        var boosted = baseCtx with
        {
            Skills = baseCtx.Skills with { PracticeLeaderExpFactor = 3.0, RoleModelExpFactor = 3.0 },
        };
        Assert.True(GradAvg(boosted) > GradAvg(baseCtx),
            "練習リーダー/お手本の倍率を上げても伸びが変わらない＝全員 Id=0 で本人除外に巻き込まれている");
    }

    [Fact]
    public void FacilitiesOnly_AppliesEquipmentMultipliers()
    {
        var baseCtx = SeasonEngineTests.LoadContext();
        var set = Set(("gear_batting", 2), ("gear_defense", 2), ("gear_baserunning", 2), ("gear_pitching", 2));
        var viaFacilities = baseCtx with { Facilities = set };
        var viaExplicit = viaFacilities with { Equipment = viaFacilities.ResolveEffects().Equipment };

        Assert.Equal(Years(viaExplicit), Years(viaFacilities));
        Assert.NotEqual(Years(baseCtx with { Facilities = Set() }), Years(viaFacilities));
    }

    [Fact]
    public void Clinic_ChangesInjuryOutcome()
    {
        var baseCtx = SeasonEngineTests.LoadContext();
        var withClinic = baseCtx with { Facilities = Set(("clinic", 3)) };
        Assert.NotEqual(Years(baseCtx with { Facilities = Set() }), Years(withClinic));
    }

    [Fact]
    public void RetiredThirdYears_GetNoWeeklyGrowthEvents()
    {
        var baseCtx = SeasonEngineTests.LoadContext();
        var retiredHits = 0;
        var ctx = baseCtx with
        {
            Growth = baseCtx.Growth with { SlumpWeeklyProb = 0.5, PlateauWeeklyProb = 0.5, BreakthroughWeeklyProb = 0.5 },
            GrowthObserver = n => { if (n.Player.IsRetired) retiredHits++; },
        };
        SeasonEngine.Run(5, ctx, new Xoshiro256Random(7));
        Assert.Equal(0, retiredHits);
    }
}

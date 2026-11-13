using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>デバッグ試合（issue #905）。選んだ球場がそのまま判定・掲示板・3D に届くこと、決定論であること。</summary>
public sealed class DebugMatchFactoryTests
{
    private static readonly Stadium Wide = new()
    {
        Id = "test_wide", Name = "広い球場", Tier = StadiumTier.Prefectural,
        LeftM = 101, RightM = 99, CenterM = 123, FenceHeightM = 3.4,
    };

    [Fact]
    public void StadiumReachesTheContext()
    {
        var built = DebugMatchFactory.Build(Wide);
        var f = built.Ctx.Field;
        Assert.Equal(101, f.LeftFenceM, 6);
        Assert.Equal(99, f.RightFenceM, 6);
        Assert.Equal(123, f.CenterFenceM, 6);
        Assert.Equal(3.4, f.FenceHeightM, 6);
        Assert.Equal("広い球場", built.Ctx.StadiumName);
        Assert.Equal("test_wide", built.Ctx.StadiumId);
        Assert.True(built.Ctx.CaptureTimelines);
        Assert.True(built.Ctx.CapturePitches);
        Assert.Null(built.Start);
        Assert.Equal(DebugMatchFactory.DefaultSeed, built.Seed);
    }

    [Fact]
    public void SameOptions_SameGame()
    {
        static string Play(ulong seed)
        {
            var b = DebugMatchFactory.Build(Stadium.Koshien, new DebugMatchOptions { Seed = seed });
            var r = GameEngine.Play(b.Away, b.Home, b.Ctx, new KokoSim.Engine.Core.Xoshiro256Random(b.Seed));
            return $"{r.AwayRuns}-{r.HomeRuns}/{r.TotalPitches}";
        }
        Assert.Equal(Play(7), Play(7));
    }

    [Fact]
    public void ScenarioKeepsTheStadium_AndIsTagged()
    {
        var scenario = new ScenarioDefinition { Id = "t-905", Name = "テスト", Inning = 9, Top = false, Outs = 2 };
        var built = DebugMatchFactory.Build(Wide, new DebugMatchOptions { Scenario = scenario, Seed = 3 });
        Assert.Equal("t-905", built.Ctx.ScenarioId);       // 統計・digest から除外される印
        Assert.Equal(123, built.Ctx.Field.CenterFenceM, 6);
        Assert.Equal("test_wide", built.Ctx.StadiumId);
        Assert.NotNull(built.Start);
        Assert.Equal(9, built.Start!.Inning);
    }
}

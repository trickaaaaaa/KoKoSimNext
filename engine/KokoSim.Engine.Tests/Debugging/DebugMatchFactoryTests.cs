using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
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

    [Fact]
    public void EveryPlayerCarriesAllPitchTypes_KeepingOwnRanks()
    {
        var rng = new KokoSim.Engine.Core.Xoshiro256Random(11);
        var raw = StrengthTeamFactory.Create(DebugMatchFactory.TeamStrength, "元", rng.Fork(1));
        var full = DebugMatchFactory.WithAllPitchTypes(raw);
        var all = System.Enum.GetValues<PitchType>();

        for (int i = 0; i < raw.BattingOrder.Count; i++)
        {
            var before = raw.BattingOrder[i].Pitching.EffectiveRepertoire;
            var after = full.BattingOrder[i].Pitching.EffectiveRepertoire;
            Assert.Equal(all.Length, after.Count);
            Assert.All(all, t => Assert.Contains(after, s => s.Type == t));
            foreach (var own in before)
                Assert.Equal(own, after.Single(s => s.Type == own.Type));   // 持っていた球種は手を付けない
        }
        Assert.All(full.Reserves, p => Assert.Equal(all.Length, p.Pitching.EffectiveRepertoire.Count));
        if (full.Captain is { } c) Assert.Contains(c, full.BattingOrder.Concat(full.Reserves));

        var built = DebugMatchFactory.Build(Stadium.Koshien);
        var pitcher = built.Home.BattingOrder[built.Home.PitcherSlot];
        Assert.Contains(pitcher.Pitching.EffectiveRepertoire, s => s.Type == PitchType.Fork);

        // 実際に投げられる＝配球が全球種から選ぶ（絵の確認に使える）
        var r = GameEngine.Play(built.Away, built.Home, built.Ctx, new KokoSim.Engine.Core.Xoshiro256Random(built.Seed));
        var thrown = r.Log.Where(e => e.PitchLog != null).SelectMany(e => e.PitchLog!).Select(p => p.PitchType).Distinct().ToList();
        Assert.Contains(PitchType.Fork, thrown);
        Assert.True(thrown.Count >= 7, "投げた球種: " + string.Join(",", thrown));
    }
}

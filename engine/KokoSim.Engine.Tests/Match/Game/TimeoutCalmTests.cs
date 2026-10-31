using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 伝令の手応え（issue #714・設計書09 §3）。
/// ・伝令の効果窓のあいだ「落ち着き」倍率が掛かる（プレッシャーが高いほど大きい・精神力に関係なく1以上）。
/// ・効き目は監督の采配能力と選手の素直さで増減する。
/// ・動揺中の投手は制球が落ちる。
/// ・采配能力の出どころは チームの明示 → 采配AIのプロファイル → 中立値 の順。
/// </summary>
public sealed class TimeoutCalmTests
{
    private static readonly TacticsCoefficients C = new();

    [Theory]
    [InlineData(0, 1.01)]
    [InlineData(5, 1.06)]
    [InlineData(8, 1.09)]
    public void Calm_GrowsWithPressure_AtNeutralSense(int pressure, double expected)
        => Assert.Equal(expected, PressureModel.CalmMultiplier(pressure, C.TimeoutSenseNeutral, 1.0, C), 10);

    [Fact]
    public void Calm_ScalesWithTacticalSense()
    {
        var low = PressureModel.CalmMultiplier(5, 20, 1.0, C);
        var mid = PressureModel.CalmMultiplier(5, 50, 1.0, C);
        var high = PressureModel.CalmMultiplier(5, 90, 1.0, C);
        Assert.True(low < mid && mid < high);
        Assert.Equal(0.79, PressureModel.SenseFactor(20, C), 10);
        Assert.Equal(1.28, PressureModel.SenseFactor(90, C), 10);
    }

    [Fact]
    public void Calm_ScalesWithReceptivity()
    {
        var pc = new PersonalityCoefficients();
        var honor = PressureModel.CalmMultiplier(5, 50, pc.Profile(Personality.HonorStudent).CoachingReceptivity, C);
        var genius = PressureModel.CalmMultiplier(5, 50, pc.Profile(Personality.Genius).CoachingReceptivity, C);
        Assert.True(honor > genius, $"素直な性格ほど伝令がよく効くはず（優等生{honor} vs 天才肌{genius}）");
    }

    [Fact]
    public void Calm_ZeroCoefficients_IsIdentity()
    {
        var off = new TacticsCoefficients { TimeoutCalmBase = 0.0, TimeoutCalmPerPressure = 0.0 };
        Assert.Equal(1.0, PressureModel.CalmMultiplier(8, 90, 1.1, off));
    }

    [Fact]
    public void Rattled_PenaltyLowersControl()
    {
        var p = new PitcherAttributes { Control = 60 };
        var rattled = PressureModel.ApplyPitcher(p, 1.0 - C.RattledControlPenalty);
        Assert.Equal(54, rattled.Control);
    }

    private static Team TeamWith(ITacticsBrain? brain, int? sense)
    {
        var order = new Player[9];
        for (var i = 0; i < 9; i++) order[i] = new Player { Name = "P" + i, Position = (KokoSim.Engine.Match.Field.FieldPosition)i };
        return new Team { Name = "T", BattingOrder = order, PitcherSlot = 0, Tactics = brain, ManagerTacticalSense = sense };
    }

    [Fact]
    public void Sense_PrefersExplicitThenAiProfileThenNeutral()
    {
        var ai = new AiTacticsBrain(new AiProfile(80, 5, SchoolStyle.Standard));
        Assert.Equal(30, new TeamState(TeamWith(ai, 30)).TimeoutSense(50));
        Assert.Equal(80, new TeamState(TeamWith(ai, null)).TimeoutSense(50));
        Assert.Equal(50, new TeamState(TeamWith(null, null)).TimeoutSense(50));
    }
}

using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #1229: 高速試合モードの出入りで UI が「窓で止まっているエンジンへ AdvancePitch を呼んで窓を飛ばす」のを
/// 避けるための読み口 <see cref="MatchProgression.IsAtPitchWindow"/>。窓で止まっている間だけ true になる。
/// </summary>
public sealed class PitchWindowStateTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(
                new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
                System.Array.Empty<Player>()),
        };
    }

    private static MatchProgression New(ulong seed) =>
        new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true, CapturePitches = true }, seed);

    [Fact]
    public void NotAtWindow_BeforeFirstAdvance()
    {
        Assert.False(New(1UL).IsAtPitchWindow);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    public void AtWindow_AfterPitchResult_UntilNextAdvance(ulong seed)
    {
        var prog = New(seed);

        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());
        Assert.True(prog.IsAtPitchWindow);
        Assert.NotNull(prog.CurrentAtBat);

        // 窓で止まっているのに打席ごと進める（高速試合モード）と窓は過ぎ去る
        prog.Advance();
        Assert.False(prog.IsAtPitchWindow);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    public void FollowsEveryAdvancePitchResult(ulong seed)
    {
        var prog = New(seed);
        var guard = 0;
        while (guard++ < 2000)
        {
            var r = prog.AdvancePitch();
            Assert.Equal(r == AdvancePitchResult.Pitch, prog.IsAtPitchWindow);
            if (r == AdvancePitchResult.Finished) return;
        }
        Assert.Fail("試合が終わらない");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(42UL)]
    public void AfterInningEndFastForward_NextAdvancePitchOpensWindowWithoutThrowing(ulong seed)
    {
        var prog = New(seed);
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());

        prog.AdvanceUntilInningEnd();
        Assert.False(prog.IsAtPitchWindow);

        // 半回の終わりは打席の境界＝次の AdvancePitch は球を投げずに次の半回の頭の窓を開く
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());
        Assert.True(prog.IsAtPitchWindow);
        Assert.Equal(1, prog.CurrentAtBat!.PitchNumberInPa);
    }

    [Fact]
    public void SeekTo_ClearsWindowState()
    {
        var prog = New(7UL);
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());
        prog.SeekTo(0);
        Assert.False(prog.IsAtPitchWindow);
    }
}

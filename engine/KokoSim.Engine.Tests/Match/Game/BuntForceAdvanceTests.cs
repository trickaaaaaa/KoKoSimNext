using System.Collections.Generic;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// バント決着の塁の付け替え（issue #1205）。押し出し（フォース）を扱う: 満塁の内野安打・犠打は三塁走者が生還し、
/// 封殺の後も押し出される走者は進み、誰も進まないバントは犠打にしない。
/// </summary>
public sealed class BuntForceAdvanceTests
{
    private static Player P(string n) => new() { Name = n };

    private static (BaseState Bases, Player B, Player? R1, Player? R2, Player? R3) Setup(bool first, bool second, bool third)
    {
        var r1 = first ? P("r1") : null;
        var r2 = second ? P("r2") : null;
        var r3 = third ? P("r3") : null;
        return (new BaseState { First = r1, Second = r2, Third = r3 }, P("b"), r1, r2, r3);
    }

    [Fact]
    public void InfieldHit_BasesLoaded_PushesEveryoneAndThirdRunnerScores()
    {
        var (bases, b, r1, r2, r3) = Setup(true, true, true);
        var (scorer, advanced) = BaserunningModel.ApplyBunt(bases, b, BuntResult.InfieldHit);
        Assert.Same(r3, scorer);
        Assert.True(advanced);
        Assert.Same(b, bases.First);
        Assert.Same(r1, bases.Second);
        Assert.Same(r2, bases.Third);
    }

    [Fact]
    public void Sacrifice_BasesLoaded_ThirdRunnerScoresAndBatterIsOut()
    {
        var (bases, b, r1, r2, r3) = Setup(true, true, true);
        var (scorer, advanced) = BaserunningModel.ApplyBunt(bases, b, BuntResult.SacrificeSuccess);
        Assert.Same(r3, scorer);
        Assert.True(advanced);
        Assert.Null(bases.First);
        Assert.Same(r1, bases.Second);
        Assert.Same(r2, bases.Third);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void Sacrifice_NobodyCanAdvance_IsNotAdvance(bool first, bool second, bool third)
    {
        var (bases, b, r1, r2, r3) = Setup(first, second, third);
        var (scorer, advanced) = BaserunningModel.ApplyBunt(bases, b, BuntResult.SacrificeSuccess);
        Assert.Null(scorer);
        Assert.False(advanced);
        Assert.Same(r1, bases.First);
        Assert.Same(r2, bases.Second);
        Assert.Same(r3, bases.Third);
    }

    [Fact]
    public void Sacrifice_FirstAndThird_FirstAdvancesThirdHolds()
    {
        var (bases, b, r1, _, r3) = Setup(true, false, true);
        var (scorer, advanced) = BaserunningModel.ApplyBunt(bases, b, BuntResult.SacrificeSuccess);
        Assert.Null(scorer);
        Assert.True(advanced);
        Assert.Null(bases.First);
        Assert.Same(r1, bases.Second);
        Assert.Same(r3, bases.Third);
    }

    [Fact]
    public void Sacrifice_SecondOnly_AdvancesToThird()
    {
        var (bases, b, _, r2, _) = Setup(false, true, false);
        var (_, advanced) = BaserunningModel.ApplyBunt(bases, b, BuntResult.SacrificeSuccess);
        Assert.True(advanced);
        Assert.Same(r2, bases.Third);
        Assert.Null(bases.Second);
    }

    [Fact]
    public void ForceOutAtSecond_FirstAndSecond_SecondRunnerIsPushedToThird()
    {
        var (bases, b, _, r2, _) = Setup(true, true, false);
        var (scorer, _) = BaserunningModel.ApplyBunt(bases, b, BuntResult.ForceOutAtSecond);
        Assert.Null(scorer);
        Assert.Same(b, bases.First);
        Assert.Null(bases.Second);
        Assert.Same(r2, bases.Third);
    }

    [Fact]
    public void ForceOutAtSecond_BasesLoaded_ThirdRunnerScores()
    {
        var (bases, b, _, r2, r3) = Setup(true, true, true);
        var (scorer, _) = BaserunningModel.ApplyBunt(bases, b, BuntResult.ForceOutAtSecond);
        Assert.Same(r3, scorer);
        Assert.Same(b, bases.First);
        Assert.Null(bases.Second);
        Assert.Same(r2, bases.Third);
    }

    [Fact]
    public void ForceOutAtSecond_FirstAndThird_ThirdRunnerHolds()
    {
        var (bases, b, _, _, r3) = Setup(true, false, true);
        var (scorer, _) = BaserunningModel.ApplyBunt(bases, b, BuntResult.ForceOutAtSecond);
        Assert.Null(scorer);
        Assert.Same(b, bases.First);
        Assert.Null(bases.Second);
        Assert.Same(r3, bases.Third);
    }

    [Fact]
    public void ForceOutAtThird_BasesLoaded_ThirdRunnerScores()
    {
        var (bases, b, r1, _, r3) = Setup(true, true, true);
        var (scorer, _) = BaserunningModel.ApplyBunt(bases, b, BuntResult.ForceOutAtThird);
        Assert.Same(r3, scorer);
        Assert.Same(b, bases.First);
        Assert.Same(r1, bases.Second);
        Assert.Null(bases.Third);
    }

    [Fact]
    public void ForceOutAtHome_BasesLoaded_NoRunAndOthersAdvance()
    {
        var (bases, b, r1, r2, _) = Setup(true, true, true);
        var (scorer, _) = BaserunningModel.ApplyBunt(bases, b, BuntResult.ForceOutAtHome);
        Assert.Null(scorer);
        Assert.Same(b, bases.First);
        Assert.Same(r1, bases.Second);
        Assert.Same(r2, bases.Third);
    }

    [Fact]
    public void Squeeze_ThirdRunnerAlreadyResolved_ChainSeesFirstAndSecondOnly()
    {
        // スクイズは三塁走者を別途裁定して外してから呼ぶ。
        var (bases, b, r1, r2, _) = Setup(true, true, true);
        bases.Third = null;
        var (scorer, _) = BaserunningModel.ApplyBunt(bases, b, BuntResult.InfieldHit);
        Assert.Null(scorer);
        Assert.Same(b, bases.First);
        Assert.Same(r1, bases.Second);
        Assert.Same(r2, bases.Third);
    }

    [Fact]
    public void PopOut_LeavesRunnersAndDoubledOffRemovesFirst()
    {
        var (bases, b, _, r2, r3) = Setup(true, true, true);
        BaserunningModel.ApplyBunt(bases, b, BuntResult.PopOut);
        Assert.Equal(3, bases.RunnerCount);
        BaserunningModel.ApplyBunt(bases, b, BuntResult.PopOutDoubledOff);
        Assert.Null(bases.First);
        Assert.Same(r2, bases.Second);
        Assert.Same(r3, bases.Third);
    }

    // ── 試合を通した確認 ──

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50, Bunt = 50,
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
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    private static LivePlateAppearance? BuntOnce(ulong seed, int outs)
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), seed,
            new ScenarioStart { Inning = 1, IsTop = true, Outs = outs, OnFirst = true, OnSecond = true, OnThird = true });
        prog.ForceNext(ForcedOutcome.BuntInPlay);
        if (prog.AdvancePitch() != AdvancePitchResult.Pitch) return null;
        prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Bunt);
        return prog.AdvancePitch() == AdvancePitchResult.PlateAppearance ? prog.Current : null;
    }

    [Fact]
    public void BasesLoaded_BuntInfieldHit_BatterStaysOnAndThirdRunnerScores()
    {
        var singles = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var pa = BuntOnce(seed, outs: 0);
            if (pa is null || pa.Result != PlateAppearanceResult.Single) continue;
            singles++;
            Assert.Equal(1, pa.RunsScored);
            Assert.True(pa.BaseFirstAfter && pa.BaseSecondAfter && pa.BaseThirdAfter);
        }
        Assert.True(singles > 0, "満塁の内野安打が1度も出なかった（探索範囲を広げる必要あり）");
    }

    [Fact]
    public void BasesLoaded_TwoOuts_BuntOutThatEndsTheInning_ScoresNothing()
    {
        var outsSeen = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var pa = BuntOnce(seed, outs: 2);
            if (pa is null || pa.Result != PlateAppearanceResult.InPlayOut) continue;
            outsSeen++;
            Assert.Equal(0, pa.RunsScored);
        }
        Assert.True(outsSeen > 0);
    }
}

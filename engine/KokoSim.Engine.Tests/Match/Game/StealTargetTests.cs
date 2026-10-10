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
/// issue #622: 監督が三盗・本盗を指示できる（engine は #67 で解決できるのに、UI が二盗しか
/// 渡していなかった）。狙う塁の判定は <see cref="StealTargets"/> が単一ソースで、
/// engine の解決分岐も采配カードの可否・ラベルもここを引く。
/// </summary>
public sealed class StealTargetTests
{
    // ---- 狙える塁の表（issue 本文の表と対） ----

    [Theory]
    // 一塁に走者・二塁が空 → 二盗（三塁走者の有無は問わない＝一・三塁を含む）
    [InlineData(true, false, false, StealTarget.Second)]
    [InlineData(true, false, true, StealTarget.Second)]
    // 二塁のみ → 三盗
    [InlineData(false, true, false, StealTarget.Third)]
    // 三塁のみ → 本盗
    [InlineData(false, false, true, StealTarget.Home)]
    public void FromBases_PicksTheOnlyFeasibleBase(bool first, bool second, bool third, StealTarget expected)
        => Assert.Equal(expected, StealTargets.FromBases(first, second, third));

    [Theory]
    [InlineData(false, false, false)]   // 走者なし
    [InlineData(true, true, false)]     // 一・二塁
    [InlineData(false, true, true)]     // 二・三塁
    [InlineData(true, true, true)]      // 満塁
    public void FromBases_ReturnsNull_WhenNoSingleStealIsPossible(bool first, bool second, bool third)
        => Assert.Null(StealTargets.FromBases(first, second, third));

    // ---- 実際に走るか（1球進行で予約 → その球で解決する） ----

    /// <summary>二塁のみの場面で三盗を予約すると、その球で走者が二塁を離れる。</summary>
    [Fact]
    public void ThirdSteal_ResolvesOnTheNextPitch()
    {
        var (moved, attempts) = StealFromScenario(
            new ScenarioStart { Inning = 1, IsTop = true, Outs = 0, OnSecond = true },
            StealTarget.Third,
            (before, after) => !after.BaseSecond && (after.BaseThird || after.Outs > before.Outs));

        Assert.True(moved > 0, "三盗を予約しても走者が一度も動かない");
        Assert.True(attempts > 0, "盗塁企図として記録されていない");
    }

    /// <summary>三塁のみの場面で本盗を予約すると、その球で走者が三塁を離れる（生還かアウト）。</summary>
    [Fact]
    public void HomeSteal_ResolvesOnTheNextPitch()
    {
        var (moved, attempts) = StealFromScenario(
            new ScenarioStart { Inning = 1, IsTop = true, Outs = 0, OnThird = true },
            StealTarget.Home,
            (before, after) => !after.BaseThird);

        Assert.True(moved > 0, "本盗を予約しても走者が一度も動かない");
        Assert.True(attempts > 0, "盗塁企図として記録されていない");
    }

    /// <summary>
    /// 塁状況に合わない狙い（二塁のみで二盗を予約）は engine が解決しない＝走者は動かない。
    /// UI がこの組み合わせを押せないようにしている根拠（<see cref="StealTargets.FromBases"/>）を
    /// engine 側からも固定する。
    /// </summary>
    [Fact]
    public void SecondSteal_DoesNothing_WhenTheRunnerIsOnSecond()
    {
        var (moved, attempts) = StealFromScenario(
            new ScenarioStart { Inning = 1, IsTop = true, Outs = 0, OnSecond = true },
            StealTarget.Second,
            (before, after) => !after.BaseSecond && (after.BaseThird || after.Outs > before.Outs));

        Assert.Equal(0, moved);
        Assert.Equal(0, attempts);
    }

    /// <summary>
    /// 場面を作って初球に盗塁を予約し、「その球で走者が動いたか」を複数シードで数える。
    ///
    /// <para>ファウル・打球の球は盗塁が巻き戻る（issue #626）ので、動かない球があること自体は正常。
    /// ここで見るのは<b>一度でも成立するか</b>と、成立した球が記録に残るか。</para>
    /// </summary>
    private static (int Moved, int Attempts) StealFromScenario(
        ScenarioStart start, StealTarget target, System.Func<LivePitch, LivePitch, bool> movedAway)
    {
        var moved = 0;
        var attempts = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true, CapturePitches = true }, seed, start);
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;

            var before = Snapshot(start);
            prog.SetStealAttempt(offenseIsAway: true, StartType.Gamble, target);
            prog.AdvancePitch();

            var after = prog.LastPitch;
            if (after is null) continue;
            if (movedAway(before, after)) moved++;
            attempts += prog.BuildResult().AwayTactics.StealAttempts;
        }
        return (moved, attempts);
    }

    /// <summary>場面ジャンプの初期塁を LivePitch と同じ形にした比較元。</summary>
    private static LivePitch Snapshot(ScenarioStart start) => new()
    {
        Pitch = new PitchToken(PitchKind.Ball, 0, 0),
        Outs = start.Outs,
        BaseFirst = start.OnFirst,
        BaseSecond = start.OnSecond,
        BaseThird = start.OnThird,
    };

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 80, ArmStrength = 50, Fielding = 50, Catching = 50, Steal = 80,   // 走力・盗塁高め
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
}

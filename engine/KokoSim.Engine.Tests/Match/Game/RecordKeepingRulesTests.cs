using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #1209: 試合記録が公認野球規則・表示の約束とずれていた4件の回帰。
/// 投手成績の登板順／失策・併殺打の打点（9.04）／サヨナラ打の塁打（9.06(f)）／アウト0の防御率／同姓代打の行まとめ。
/// </summary>
public sealed class RecordKeepingRulesTests
{
    private static GameResult PlayForced(ScenarioStart scenario, ForcedOutcome outcome, ulong seed = 1UL)
    {
        var prog = new MatchProgression(
            DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), seed, scenario);
        prog.ForceNext(outcome);
        while (prog.Advance()) { }
        return prog.BuildResult();
    }

    /// <summary>強制した最初の1打席だけ進めた時点の結果（その後の打席の打点が混ざらない）。</summary>
    private static GameResult PlayFirstPaOnly(ScenarioStart scenario, ForcedOutcome outcome, ulong seed)
    {
        var prog = new MatchProgression(
            DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), seed, scenario);
        prog.ForceNext(outcome);
        prog.Advance();
        return prog.BuildResult();
    }

    private static ScenarioStart TiedBottom9(bool first, bool second, bool third) => new()
    {
        Inning = 9, IsTop = false, Outs = 0,
        OnFirst = first, OnSecond = second, OnThird = third,
        AwayScore = 6, HomeScore = 6,
    };

    // ===== 1. 投手成績は登板順 =====

    [Fact]
    public void PitchingLines_AreInOrderOfAppearance_NotReserveOrder()
    {
        var state = new TeamState(DeterminismCards.Team("A"));
        var reserves = state.AvailablePitcherCandidates;
        Assert.True(reserves.Count >= 2);
        var starter = state.CurrentPitcher;
        var first = reserves[^1];   // 背番号順では後ろの控えが先に投げる
        var second = reserves[0];

        state.RecordPitching(starter, PlateAppearanceResult.Strikeout, runs: 0, outs: 1, pitches: 3);
        Assert.True(state.ChangePitcherTo(first));
        state.RecordPitching(state.CurrentPitcher, PlateAppearanceResult.Strikeout, runs: 0, outs: 1, pitches: 3);
        Assert.True(state.ChangePitcherTo(second));
        state.RecordPitching(state.CurrentPitcher, PlateAppearanceResult.Strikeout, runs: 0, outs: 1, pitches: 3);

        var names = state.BuildPitchingLines().Select(l => l.Name).ToList();
        Assert.Equal(new[] { starter.Name, first.Name, second.Name }, names);
    }

    // ===== 2. 失策・併殺打の打点（規則 9.04） =====

    [Fact]
    public void ReachedOnError_TwoOuts_RunScoresWithoutRbi()
    {
        var scenario = new ScenarioStart
        {
            Inning = 5, IsTop = false, Outs = 2, OnFirst = true, OnSecond = true, OnThird = true,
            AwayScore = 0, HomeScore = 0,
        };
        var scored = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var r = PlayFirstPaOnly(scenario, ForcedOutcome.ReachedOnError, seed);
            var e = r.Log.First(x => x.Inning == 5 && !x.IsTop);
            if (e.Result != PlateAppearanceResult.ReachedOnError || e.RunsScored == 0) continue;
            scored++;
            Assert.Equal(0, r.HomeBatting.Sum(b => b.Rbi));
        }
        Assert.True(scored > 0, "失策で得点が入ったシードが無い＝何も検証していない");
    }

    [Fact]
    public void ReachedOnError_NoOuts_OnlyRunnerFromThirdGetsRbi()
    {
        // 無死二三塁の失策出塁: 三塁走者の分だけが打点。二塁走者の得点は打点にしない。
        var scenario = new ScenarioStart
        {
            Inning = 5, IsTop = false, Outs = 0, OnFirst = false, OnSecond = true, OnThird = true,
            AwayScore = 0, HomeScore = 0,
        };
        var twoScored = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var r = PlayFirstPaOnly(scenario, ForcedOutcome.ReachedOnError, seed);
            var e = r.Log.First(x => x.Inning == 5 && !x.IsTop);
            if (e.Result != PlateAppearanceResult.ReachedOnError || e.RunsScored < 2) continue;
            twoScored++;
            Assert.Equal(1, r.HomeBatting.Sum(b => b.Rbi));
        }
        Assert.True(twoScored > 0, "二人還ったシードが無い＝何も検証していない");
    }

    [Fact]
    public void ForceDoublePlay_RunScoresWithoutRbi()
    {
        // 無死一三塁の併殺打: 三塁走者が還っても打点は付かない。
        var scenario = new ScenarioStart
        {
            Inning = 5, IsTop = false, Outs = 0, OnFirst = true, OnSecond = false, OnThird = true,
            AwayScore = 0, HomeScore = 0,
        };
        var scored = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var r = PlayFirstPaOnly(scenario, ForcedOutcome.DoublePlay, seed);
            var e = r.Log.First(x => x.Inning == 5 && !x.IsTop);
            if (e.OutsThisPa < 2 || e.RunsScored == 0) continue;
            scored++;
            Assert.Equal(0, r.HomeBatting.Sum(b => b.Rbi));
        }
        Assert.True(scored > 0, "併殺で得点が入ったシードが無い＝何も検証していない");
    }

    // ===== 3. サヨナラ打の塁打（規則 9.06(f)） =====

    [Fact]
    public void WalkoffHitResult_CapsBasesAtWinningRunnersAdvance()
    {
        var order = DeterminismCards.Team("X").BattingOrder;
        var third = new RunnerMove(order[0], 3, 4, false);
        var second = new RunnerMove(order[1], 2, 4, false);
        var first = new RunnerMove(order[2], 1, 4, false);
        var batter = new RunnerMove(order[3], 0, 2, false);

        // 三塁走者が二塁打で還る＝単打
        Assert.Equal(PlateAppearanceResult.Single,
            GameEngine.WalkoffHitResult(PlateAppearanceResult.Double, new[] { third, batter }, 1));
        // 二塁走者が三塁打で還る＝二塁打
        Assert.Equal(PlateAppearanceResult.Double,
            GameEngine.WalkoffHitResult(PlateAppearanceResult.Triple, new[] { second, batter }, 1));
        // 一塁走者が二塁打で還る（3塁進塁）＝二塁打のまま
        Assert.Equal(PlateAppearanceResult.Double,
            GameEngine.WalkoffHitResult(PlateAppearanceResult.Double, new[] { first, batter }, 1));
        // 勝ち越しに要る人数に届かなければ（サヨナラでない）そのまま
        Assert.Equal(PlateAppearanceResult.Double,
            GameEngine.WalkoffHitResult(PlateAppearanceResult.Double, new[] { third, batter }, 2));
        // 2点要る＝前の走者から2人目（二塁走者）が勝ち越し点
        Assert.Equal(PlateAppearanceResult.Double,
            GameEngine.WalkoffHitResult(PlateAppearanceResult.Triple, new[] { third, second, batter }, 2));
        // 本塁打は対象外
        Assert.Equal(PlateAppearanceResult.HomeRun,
            GameEngine.WalkoffHitResult(PlateAppearanceResult.HomeRun, new[] { third, batter }, 1));
    }

    [Fact]
    public void WalkoffDouble_ThirdRunnerScores_IsRecordedAsSingle()
    {
        var r = PlayForced(TiedBottom9(first: false, second: false, third: true), ForcedOutcome.Double);
        Assert.Equal(7, r.HomeRuns);
        Assert.Equal(1, r.HomeBatting.Sum(b => b.Hits));
        Assert.Equal(0, r.HomeBatting.Sum(b => b.Doubles));
        Assert.Equal(1, r.HomeBatting.Sum(b => b.Rbi));
    }

    // ===== 4. アウト0の防御率 =====

    [Fact]
    public void Era_ZeroOutsWithEarnedRuns_IsNotZero()
    {
        var line = new PitchingStatLine();
        line.Add(new PitchingLine("P", 0, 2, 2, 2, 0, 0, 10, null, 0, 0, 0, null, 2), started: false);
        Assert.True(double.IsPositiveInfinity(line.Era));
        Assert.Equal("-", line.EraText);
    }

    [Fact]
    public void Era_WithOuts_IsFormatted()
    {
        var line = new PitchingStatLine();
        line.Add(new PitchingLine("P", 27, 30, 5, 2, 8, 1, 100, null, 0, 0, 0, null, 2), started: true);
        Assert.Equal("2.00", line.EraText);
    }

    // ===== 5. 同姓の代打は別の行 =====

    [Fact]
    public void BoxScoreGrid_SameNameDifferentSourceId_AreSeparateRows()
    {
        var log = new List<PlayLogEntry>
        {
            Entry(batterSourceId: 1, name: "田中"),
            Entry(batterSourceId: 2, name: "田中"),
            Entry(batterSourceId: 1, name: "田中"),
        };
        var rows = BoxScoreGrid.Build(log, isTop: true);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].Cells.Count);
        Assert.Single(rows[1].Cells);
    }

    private static PlayLogEntry Entry(int batterSourceId, string name)
        => new(1, true, name, PlateAppearanceResult.Strikeout, 0, null, 3, 0,
            false, false, false, false, false, false, 4, batterSourceId);
}

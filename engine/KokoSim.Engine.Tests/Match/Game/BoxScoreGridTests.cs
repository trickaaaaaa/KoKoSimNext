using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 試合結果画面（Issue #13）の供給部品を検証する。
/// 打席グリッド組み替え（BoxScoreGrid）と勝敗投手の添字判定（DecisionOfRecord）。いずれも表示専用の純関数。
/// </summary>
public sealed class BoxScoreGridTests
{
    private static PlayLogEntry Pa(
        int inning, bool isTop, string name, PlateAppearanceResult result,
        int order, int runs = 0, int? sourceId = null)
        => new(inning, isTop, name, result, runs, BatterOrder: order, BatterSourceId: sourceId);

    // ── 打席グリッド ──

    [Fact]
    public void Build_GroupsByOrder_AndNumbersPlateAppearances()
    {
        var log = new List<PlayLogEntry>
        {
            Pa(1, true, "A", PlateAppearanceResult.Single, 1, sourceId: 11),
            Pa(1, true, "B", PlateAppearanceResult.Strikeout, 2),
            Pa(3, true, "A", PlateAppearanceResult.HomeRun, 1, runs: 2, sourceId: 11),
            Pa(3, true, "B", PlateAppearanceResult.Walk, 2),
            Pa(5, true, "A", PlateAppearanceResult.InPlayOut, 1, sourceId: 11),
        };

        var rows = BoxScoreGrid.Build(log, isTop: true);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Order));
        Assert.Equal("A", rows[0].BatterName);
        Assert.Equal(11, rows[0].BatterSourceId);
        Assert.Equal(new[] { 1, 2, 3 }, rows[0].Cells.Select(c => c.PaIndex));
        Assert.Equal(new[] { 1, 3, 5 }, rows[0].Cells.Select(c => c.Inning));
        Assert.Equal(PlateAppearanceResult.HomeRun, rows[0].Cells[1].Result);
        Assert.Equal(2, rows[0].Cells[1].RunsScored);
        Assert.Equal(2, rows[1].Cells.Count);
        Assert.Null(rows[1].BatterSourceId);
        Assert.Equal(3, BoxScoreGrid.MaxPaIndex(rows));
    }

    [Fact]
    public void Build_SeparatesTopAndBottomHalves()
    {
        var log = new List<PlayLogEntry>
        {
            Pa(1, true, "A", PlateAppearanceResult.Single, 1),
            Pa(1, false, "X", PlateAppearanceResult.Double, 1),
            Pa(2, false, "Y", PlateAppearanceResult.Strikeout, 2),
        };

        var top = BoxScoreGrid.Build(log, isTop: true);
        var bottom = BoxScoreGrid.Build(log, isTop: false);

        Assert.Equal(new[] { "A" }, top.Select(r => r.BatterName));
        Assert.Equal(new[] { "X", "Y" }, bottom.Select(r => r.BatterName));
    }

    [Fact]
    public void Build_PinchHitter_TakesOverSlotNumbering()
    {
        // 3番枠: A が2打席 → 代打 C が3打席目を引き継ぐ。行は分かれるが打席番号は枠の通し番号。
        var log = new List<PlayLogEntry>
        {
            Pa(1, true, "A", PlateAppearanceResult.Single, 3),
            Pa(3, true, "A", PlateAppearanceResult.Strikeout, 3),
            Pa(6, true, "C", PlateAppearanceResult.Double, 3),
        };

        var rows = BoxScoreGrid.Build(log, isTop: true);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "A", "C" }, rows.Select(r => r.BatterName));   // 初出順
        Assert.All(rows, r => Assert.Equal(3, r.Order));
        Assert.Equal(new[] { 1, 2 }, rows[0].Cells.Select(c => c.PaIndex));
        Assert.Equal(new[] { 3 }, rows[1].Cells.Select(c => c.PaIndex));    // 枠の3打席目
    }

    [Fact]
    public void Build_SortsByOrder_AndPutsUnknownOrderLast()
    {
        var log = new List<PlayLogEntry>
        {
            Pa(1, true, "Z", PlateAppearanceResult.Single, 0),   // 打順不明（バント等の経路）
            Pa(1, true, "B", PlateAppearanceResult.Single, 5),
            Pa(2, true, "A", PlateAppearanceResult.Single, 2),
        };

        var rows = BoxScoreGrid.Build(log, isTop: true);

        Assert.Equal(new[] { "A", "B", "Z" }, rows.Select(r => r.BatterName));
        Assert.Equal(new[] { 2, 5, 0 }, rows.Select(r => r.Order));
    }

    [Fact]
    public void Build_EmptyLog_ReturnsNoRows()
    {
        var rows = BoxScoreGrid.Build(new List<PlayLogEntry>(), isTop: true);

        Assert.Empty(rows);
        Assert.Equal(0, BoxScoreGrid.MaxPaIndex(rows));
    }

    // ── 勝敗投手（両校ぶんの添字） ──

    private static PitchingLine Pl(string name, int outs, int? sourceId = null)
        => new(name, outs, BattersFaced: 0, Hits: 0, Runs: 0, StrikeOuts: 0, Walks: 0, Pitches: 0, SourceId: sourceId);

    private static GameResult Result(int awayRuns, int homeRuns,
        IReadOnlyList<PitchingLine> awayPitching, IReadOnlyList<PitchingLine> homePitching)
        => new()
        {
            AwayName = "A校", HomeName = "H校",
            AwayRuns = awayRuns, HomeRuns = homeRuns,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            AwayPitching = awayPitching, HomePitching = homePitching,
        };

    [Fact]
    public void ResolveIndices_HomeWin_MarksHomeStarterWin_AndAwayStarterLoss()
    {
        var r = Result(2, 5,
            awayPitching: new[] { Pl("A1", 24), Pl("A2", 3) },
            homePitching: new[] { Pl("H1", 27) });

        var (awayWin, awayLose, homeWin, homeLose) = DecisionOfRecord.ResolveIndices(r);

        Assert.Null(awayWin);
        Assert.Equal(0, awayLose);
        Assert.Equal(0, homeWin);
        Assert.Null(homeLose);
    }

    [Fact]
    public void ResolveIndices_ShortStart_GivesDecisionToLastPitcher()
    {
        // 先発1回(3アウト)で降板、2番手が8回＝先発は総アウトの半分未満 → 最終登板投手が勝ち投手。
        var r = Result(7, 1,
            awayPitching: new[] { Pl("A1", 3), Pl("A2", 24) },
            homePitching: new[] { Pl("H1", 27) });

        var (awayWin, awayLose, homeWin, homeLose) = DecisionOfRecord.ResolveIndices(r);

        Assert.Equal(1, awayWin);
        Assert.Null(awayLose);
        Assert.Null(homeWin);
        Assert.Equal(0, homeLose);
    }

    [Fact]
    public void ResolveIndices_Tie_NoDecision()
    {
        var r = Result(3, 3,
            awayPitching: new[] { Pl("A1", 27) },
            homePitching: new[] { Pl("H1", 27) });

        Assert.Equal((null, null, null, null), DecisionOfRecord.ResolveIndices(r));
    }

    [Fact]
    public void ResolveIndices_MatchesSourceIdBasedResolve()
    {
        var r = Result(4, 2,
            awayPitching: new[] { Pl("A1", 3, sourceId: 101), Pl("A2", 24, sourceId: 102) },
            homePitching: new[] { Pl("H1", 27, sourceId: 201) });

        var (awayWin, _, _, homeLose) = DecisionOfRecord.ResolveIndices(r);
        var (winPid, _) = DecisionOfRecord.Resolve(r, managerIsAway: true);
        var (_, losePid) = DecisionOfRecord.Resolve(r, managerIsAway: false);

        Assert.Equal(r.AwayPitching[awayWin!.Value].SourceId, winPid);
        Assert.Equal(r.HomePitching[homeLose!.Value].SourceId, losePid);
    }
}

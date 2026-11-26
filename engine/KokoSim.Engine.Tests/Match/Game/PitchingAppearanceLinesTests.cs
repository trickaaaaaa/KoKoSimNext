using System.Linq;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>登板ごとの投手成績（issue #1693）。同じ投手が戻れば別の行・各登板の合計は累計 PitchingLine と一致する。</summary>
public sealed class PitchingAppearanceLinesTests
{
    [Fact]
    public void AlternatingPitchers_GiveOneRowPerAppearance_AndSumToCumulativeLines()
    {
        var state = new TeamState(DeterminismCards.Team("A"));
        var a = state.CurrentPitcher;
        var f = state.CurrentLineup.First(p => p.Position == KokoSim.Engine.Match.Field.FieldPosition.CenterField);

        // a → f → a → f → a と投野交代で交互に5登板（投手が守備へ回り、守備者が投げる＝退場しないので何度でも戻れる）
        var order = new[] { a, f, a, f, a };
        for (var i = 0; i < order.Length; i++)
        {
            if (i > 0) Assert.True(state.RelieveWith(order[i], allowBenchBackfill: false));
            Assert.Equal(order[i].Name, state.CurrentPitcher.Name);
            var p = state.CurrentPitcher;
            state.RecordPitchSpeed(p, 130 + i);
            state.RecordPitching(p, PlateAppearanceResult.Strikeout, runs: 0, outs: 1, pitches: 4 + i);
            state.RecordPitching(p, PlateAppearanceResult.Single, runs: i, outs: 0, pitches: 3, unearnedRuns: i % 2);
            state.RecordRunnerOut(p);
            state.RecordUnresolvedPitches(p, 2);
        }
        // 引き継ぎ走者の失点は前の投手へ付く（その投手の最後の登板）
        state.ChargeRuns(a, 1);

        var appearances = state.BuildPitchingAppearanceLines();
        Assert.Equal(order.Select(p => p.Name), appearances.Select(s => s.Name));

        var lines = state.BuildPitchingLines();
        Assert.Equal(2, lines.Count);
        foreach (var line in lines)
        {
            var mine = appearances.Where(s => s.Name == line.Name).ToList();
            Assert.Equal(line.Outs, mine.Sum(s => s.Outs));
            Assert.Equal(line.BattersFaced, mine.Sum(s => s.BattersFaced));
            Assert.Equal(line.Hits, mine.Sum(s => s.Hits));
            Assert.Equal(line.Runs, mine.Sum(s => s.Runs));
            Assert.Equal(line.EarnedRuns, mine.Sum(s => s.EarnedRuns));
            Assert.Equal(line.StrikeOuts, mine.Sum(s => s.StrikeOuts));
            Assert.Equal(line.Walks, mine.Sum(s => s.Walks));
            Assert.Equal(line.HitBatters, mine.Sum(s => s.HitBatters));
            Assert.Equal(line.Pitches, mine.Sum(s => s.Pitches));
            Assert.Equal(line.VelocitySamples, mine.Sum(s => s.VelocitySamples));
        }
        // 引き継ぎ分の +1 は a の最後の登板（i=4）へ付く（i=2 の登板は自分の2点のまま）
        Assert.Equal(2, appearances[2].Runs);
        Assert.Equal(5, appearances[4].Runs);
    }

    [Fact]
    public void NoPitching_GivesNoAppearances()
        => Assert.Empty(new TeamState(DeterminismCards.Team("A")).BuildPitchingAppearanceLines());

    private static GameResult Game() => new()
    {
        AwayName = "A", HomeName = "B", AwayRuns = 0, HomeRuns = 0, InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
    };

    [Fact]
    public void MatchRecord_ListsOneRowPerAppearance_InOrder()
    {
        PitchingLine L(string n, int outs) => new(n, outs, 0, 0, 0, 0, 0, 0);
        var result = Game() with
        {
            AwayPitching = new[] { L("西村", 10), L("守田", 4) },
            AwayPitchingAppearances = new[] { L("西村", 6), L("守田", 2), L("西村", 4), L("守田", 2) },
        };
        var rec = KokoSim.Engine.Stats.MatchRecordFactory.From(result, new KokoSim.Engine.Stats.MatchRecord { ManagerIsAway = true });
        Assert.Equal(new[] { "西村", "守田", "西村", "守田" }, rec.Away!.Pitching.Select(p => p.Name));
        Assert.Equal(new[] { 6, 2, 4, 2 }, rec.Away.Pitching.Select(p => p.Outs));
        // 登板ごとの列が無い試合（抽象シム等）は1人1行の累計
        var plain = KokoSim.Engine.Stats.MatchRecordFactory.From(Game() with { AwayPitching = result.AwayPitching },
            new KokoSim.Engine.Stats.MatchRecord());
        Assert.Equal(2, plain.Away!.Pitching.Count);
    }

    [Fact]
    public void WholeGames_AppearancesSumToCumulativeLines()
    {
        foreach (var card in new[] { "tactics", "pitch-tactics" })
        foreach (var seed in DeterminismCards.Seeds().Take(30))
        {
            var r = DeterminismCards.Run(card, seed);
            foreach (var (apps, lines) in new[] { (r.AwayPitchingAppearances, r.AwayPitching), (r.HomePitchingAppearances, r.HomePitching) })
            {
                Assert.Equal(lines.Sum(l => l.Outs), apps.Sum(s => s.Outs));
                Assert.Equal(lines.Sum(l => l.Runs), apps.Sum(s => s.Runs));
                Assert.Equal(lines.Sum(l => l.Pitches), apps.Sum(s => s.Pitches));
                Assert.Equal(lines.Sum(l => l.BattersFaced), apps.Sum(s => s.BattersFaced));
            }
        }
    }
}

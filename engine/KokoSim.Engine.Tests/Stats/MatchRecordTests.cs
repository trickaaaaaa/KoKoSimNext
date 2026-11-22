using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using KokoSim.Engine.Tests.Saves;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Stats;

/// <summary>自校の公式戦の戦績（issue #1413）。GameResult→記録→セーブの往復と、学校に付いて回る付け替え。</summary>
public sealed class MatchRecordTests
{
    private readonly ITestOutputHelper _out;
    public MatchRecordTests(ITestOutputHelper output) => _out = output;

    private static GameResult Play(ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var away = StrengthTeamFactory.Create(52, "先攻校", rng);
        var home = StrengthTeamFactory.Create(52, "後攻校", rng);
        return GameEngine.Play(away, home, new GameContext(), rng);
    }

    private static MatchRecord Meta(bool managerIsAway = true, int year = 2) => new()
    {
        Year = year, Kind = TournamentKind.Summer, Month = 7, Day = 13, RoundName = "3回戦",
        OpponentId = 77, OwnName = "桜丘", OpponentName = "北嶺", ManagerIsAway = managerIsAway,
    };

    [Fact]
    public void From_CarriesScoreAndBoxScoreOfBothSides()
    {
        var r = Play(11);
        var rec = MatchRecordFactory.From(r, Meta(managerIsAway: false));

        Assert.True(rec.HasDetail);
        Assert.Equal(r.HomeRuns, rec.OwnRuns);
        Assert.Equal(r.AwayRuns, rec.OpponentRuns);
        Assert.Equal("北嶺", rec.AwayName);
        Assert.Equal("桜丘", rec.HomeName);
        Assert.Equal(r.AwayLineScore, rec.Away!.LineScore);
        Assert.Equal(r.HomeLineScore, rec.Home!.LineScore);
        Assert.Equal((r.AwayHits, r.AwayErrors), (rec.Away.Hits, rec.Away.Errors));

        foreach (var (isAway, side, lines, pitching) in new[]
        {
            (true, rec.Away, r.AwayBatting, r.AwayPitching),
            (false, rec.Home, r.HomeBatting, r.HomePitching),
        })
        {
            Assert.Equal(lines.Count, side.Batting.Count);
            for (var i = 0; i < lines.Count; i++)
            {
                var b = lines[i];
                var m = side.Batting[i];
                Assert.Equal((b.Order, b.Position, b.Name, b.Entry, b.FieldedAt), (m.Order, m.Position, m.Name, m.Entry, m.FieldedAt));
                Assert.Equal((b.AtBats, b.Hits, b.Rbi, b.HomeRuns, b.Triples, b.Doubles),
                    (m.AtBats, m.Hits, m.Rbi, m.HomeRuns, m.Triples, m.Doubles));
                Assert.Equal((b.Walks + b.HitByPitches, b.StrikeOuts, b.StolenBases), (m.WalksHbp, m.StrikeOuts, m.StolenBases));
            }
            Assert.Equal(pitching.Select(p => (p.Name, p.InningsText, p.Pitches, p.Hits, p.StrikeOuts, p.Walks + p.HitBatters, p.Runs)),
                side.Pitching.Select(p => (p.Name, p.InningsText, p.Pitches, p.Hits, p.StrikeOuts, p.WalksHbp, p.Runs)));

            var grid = BoxScoreGrid.Build(r.Log, isAway);
            Assert.Equal(grid.Count, side.PaRows.Count);
            for (var i = 0; i < grid.Count; i++)
            {
                Assert.Equal((grid[i].Order, grid[i].BatterName), (side.PaRows[i].Order, side.PaRows[i].BatterName));
                Assert.Equal(
                    grid[i].Cells.Select(c => (c.PaIndex, c.Result, c.IsSacFly, c.IsSacBunt, c.OutsThisPa >= 2)),
                    side.PaRows[i].Cells.Select(c => (c.PaIndex, c.Result, c.SacFly, c.SacBunt, c.DoublePlay)));
            }
        }
    }

    [Fact]
    public void SaveRoundTrip_KeepsEveryRecordIncludingScoreOnlyRows()
    {
        var state = SaveTestFixtures.BuildRichState();
        var book = new MatchRecordBook();
        book.Add(MatchRecordFactory.From(Play(1), Meta(managerIsAway: true)));
        book.Add(MatchRecordFactory.From(Play(2), Meta(managerIsAway: false, year: 3)));
        book.Add(Meta() with { OwnRuns = 3, OpponentRuns = 2 });   // 詳細なし＝スコアだけ
        state.MatchRecords = book;

        var json = SaveCodec.Serialize(state);
        Assert.True(SaveCodec.TryDeserialize(json, out var back, out var err), err);

        Assert.Equal(3, back.MatchRecords.Count);
        Assert.False(back.MatchRecords.Records[2].HasDetail);
        Assert.Equal(3, back.MatchRecords.Records[2].OwnRuns);
        // 読み直した記録をもう一度焼いても同じ（欠落・順序入れ替わりが無い）。
        Assert.Equal(json, SaveCodec.Serialize(back));
        // 見える中身も同じ。
        for (var i = 0; i < 2; i++)
        {
            var a = book.Records[i];
            var b = back.MatchRecords.Records[i];
            Assert.Equal((a.Year, a.Kind, a.Month, a.Day, a.RoundName, a.OpponentId, a.OwnName, a.OpponentName, a.ManagerIsAway, a.OwnRuns, a.OpponentRuns),
                (b.Year, b.Kind, b.Month, b.Day, b.RoundName, b.OpponentId, b.OwnName, b.OpponentName, b.ManagerIsAway, b.OwnRuns, b.OpponentRuns));
            Assert.Equal(a.Away!.PaRows.Select(r => r.Cells.Select(c => c.Result)), b.Away!.PaRows.Select(r => r.Cells.Select(c => c.Result)));
            Assert.Equal(a.Home!.Batting.Select(x => x.Name), b.Home!.Batting.Select(x => x.Name));
        }
    }

    [Fact]
    public void OldSave_WithoutMatchRecords_LoadsWithEmptyBook()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.MatchRecords.Add(MatchRecordFactory.From(Play(3), Meta()));
        var json = SaveCodec.Serialize(state);
        var start = json.IndexOf(",\"matchRecords\":", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "matchRecords が直列化されていない");
        var end = json.IndexOf(",\"events\":", System.StringComparison.Ordinal);
        Assert.True(end > start, "events が matchRecords の後に居ない");
        var v39 = json.Remove(start, end - start).Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":39");
        Assert.DoesNotContain("\"matchRecords\"", v39);

        Assert.True(SaveCodec.TryDeserialize(v39, out var s, out var err), err);
        Assert.Equal(0, s.MatchRecords.Count);
        Assert.NotEmpty(s.TournamentHistory.Entries);   // 他の項目は素通りで読めている
    }

    [Fact]
    public void HandOverSchool_KeepsRecordsWithTheirSchool()
    {
        var book = new MatchRecordBook();
        book.Add(Meta() with { OwnName = "桜丘" });                      // いまの自校
        book.Add(Meta() with { OwnName = "北嶺", SchoolId = 500 });      // 転任先がかつて戦った戦績（前の在任）

        book.HandOverSchool(departingSchoolId: 321, arrivingSchoolId: 500);

        Assert.Equal(new[] { "桜丘" }, book.ForSchool(321).Select(r => r.OwnName));
        Assert.Equal(new[] { "北嶺" }, book.ForSchool(MatchRecordBook.ManagerKey).Select(r => r.OwnName));
        Assert.Empty(book.ForSchool(500));
    }

    [Fact]
    public void ThirtyYears_OfOfficialGames_FitsInTheSave()
    {
        // 30年・年15試合＝450試合ぶんの容量。異なる試合を60本焼いて1試合あたりを測り、450本へ掛ける。
        const int sample = 60;
        var book = new MatchRecordBook();
        for (ulong s = 0; s < sample; s++) book.Add(MatchRecordFactory.From(Play(100 + s), Meta((s & 1) == 0)));

        var state = new SaveState { MatchRecords = book };
        var empty = SaveCodec.Serialize(new SaveState());
        var json = SaveCodec.Serialize(state);
        var rawPerGame = (double)(Encoding.UTF8.GetByteCount(json) - Encoding.UTF8.GetByteCount(empty)) / sample;
        var gzPerGame = (double)(Gzip(json) - Gzip(empty)) / sample;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(SaveCodec.TryDeserialize(json, out _, out var err), err);
        var readMs = sw.ElapsedMilliseconds;

        _out.WriteLine($"1試合あたり: JSON {rawPerGame:F0} B / gzip {gzPerGame:F0} B");
        _out.WriteLine($"450試合: JSON {rawPerGame * 450 / 1024:F0} KiB / gzip {gzPerGame * 450 / 1024:F0} KiB（{sample}試合の読み込み {readMs} ms）");

        Assert.True(gzPerGame * 450 < 1_500_000, $"30年ぶんの戦績が gzip で {gzPerGame * 450 / 1024:F0} KiB と重い");
    }

    private static int Gzip(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw, 0, raw.Length);
        return (int)ms.Length;
    }
}

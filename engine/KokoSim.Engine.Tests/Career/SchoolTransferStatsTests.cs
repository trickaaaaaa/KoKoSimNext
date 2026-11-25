using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Season;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Stats;
using KokoSim.Engine.Tests.Saves;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// issue #1261: 転任で選手IDを振り直しても、学校の通算帳簿が学校に付いて回る。
/// 去る学校は在任中の帳簿を全国の通算へ据え、引き継ぐ学校は AI 時代の帳簿を新しい部員のIDへ付け替える。
/// </summary>
public sealed class SchoolTransferStatsTests
{
    private const int Year = 4;
    private const ulong Seed = 0xC0FFEEUL;
    private const int FirstManagerId = 200;

    private static GameResult OneBatter(int sourceId, int hits) => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 0, HomeRuns = 0,
        InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
        HomeBatting = new[]
        {
            new BattingLine(1, FieldPosition.CenterField, "打者", hits + 1, hits + 1, hits, 0, 0, 0, 0, 0, 0,
                SourceId: sourceId),
        },
    };

    private static List<DevelopingPlayer> ManagerRoster()
        => SaveTestFixtures.BuildManagerRoster().Where(p => !p.IsRetired).ToList();

    private static int Hits(StatBook? book, int id) => book?.Get(id)?.Batting.Hits ?? 0;

    /// <summary>転任先 AI校の部員のうち、自校の部員になったとき先頭に来る選手（学年の高い順・校内ID順）の旧ID。</summary>
    private static int FirstAiIdAfterConversion(AiSchoolRoster r)
        => r.Players.OrderByDescending(p => p.Grade).ThenBy(p => p.Id).First().Id;

    [Fact]
    public void ArrivingCareer_FollowsTheRenumberedPlayers_AndDropsNonRosterIds()
    {
        var generated = SaveTestFixtures.BuildNation();
        var overlay = new NationOverlay();
        var rosters = new NationRosters(new AiRosterDeps());
        var stats = new NationTournamentStats();
        var target = generated.InPrefecture(13).First();
        var aiRoster = rosters.GetOrBootstrap(target, Year);
        var aiId = FirstAiIdAfterConversion(aiRoster);
        stats.FoldSchool(target.Id, OneBatter(aiId, hits: 3), schoolIsAway: false);
        stats.FoldSchool(target.Id, OneBatter(9999, hits: 5), schoolIsAway: false);   // 卒業済み＝今の部員に居ない
        var roster = ManagerRoster();

        var result = SchoolTransfer.Execute(overlay, generated, rosters, new DepartingSchool("新設", 13, null, 50.0),
            roster, target, Year, FirstManagerId, Seed, stats, departingCareer: new StatBook());

        var newId = result.Roster[0].Id;
        Assert.Equal(FirstManagerId, newId);
        Assert.Equal(3, Hits(result.ArrivingCareer, newId));
        // 部員に居ないIDは持ち越さない（記録タブの個人記録に名前の引けない行が出ない）。
        Assert.All(result.ArrivingCareer.Players.Keys, id => Assert.Contains(result.Roster, p => p.Id == id));
        // 率いている間、全国側の帳簿は無い＝自校ストア（ArrivingCareer を据える先）だけが持つ。
        Assert.Null(stats.CareerForSchool(target.Id));
    }

    [Fact]
    public void DepartingCareer_BecomesTheSchoolsCareer_WithoutAiEraBleed()
    {
        var generated = SaveTestFixtures.BuildNation();
        var overlay = new NationOverlay();
        var rosters = new NationRosters(new AiRosterDeps());
        var stats = new NationTournamentStats();
        var schools = generated.InPrefecture(13).Take(2).ToList();
        var x = schools[0];
        var aiId = FirstAiIdAfterConversion(rosters.GetOrBootstrap(x, Year));
        stats.FoldSchool(x.Id, OneBatter(aiId, hits: 3), schoolIsAway: false);   // X が AI校だった頃（ID は小さい）

        // ① X を引き継ぐ。AI 時代の3安打は新しい部員（ID 200）へ付け替わる。
        var first = SchoolTransfer.Execute(overlay, generated, rosters, new DepartingSchool("新設", 13, null, 50.0),
            ManagerRoster(), x, Year, FirstManagerId, Seed, stats, departingCareer: new StatBook());
        var manager = new StatBook();
        manager.ReplaceWith(first.ArrivingCareer);
        var firstId = first.Roster[0].Id;
        Assert.Equal(3, Hits(manager, firstId));

        // 在任中にさらに2安打。
        manager.FoldGame(OneBatter(firstId, hits: 2), managerIsAway: false);

        // ② X を去る。在任中の帳簿（AI時代ぶん＋在任ぶん＝5安打）が X の通算になる。
        var second = SchoolTransfer.Execute(overlay, generated, rosters,
            new DepartingSchool(x.Name, 13, x.DistrictId, 60.0), first.Roster, schools[1], Year + 1,
            first.NextPlayerId, Seed, stats, departingCareer: manager);

        Assert.Equal(x.Id, second.OldSchoolId);
        var book = stats.CareerForSchool(x.Id);
        Assert.NotNull(book);
        Assert.Equal(5, Hits(book, firstId));
        // 古い AI 時代のID（小さい）は残らない＝今の部員のIDに重なって足されない。
        Assert.All(book!.Players.Keys, id => Assert.True(id >= FirstManagerId, $"stale id {id}"));

        // SchoolStatsLookup が X の成績として返す（自校でなくなったので全国側を引く・#634 と同じ意味の通算）。
        var lookup = new SchoolStatsLookup(managerSchoolId: -1, new PlayerStatStore(), stats);
        Assert.Equal(5, lookup.Career(x.Id, firstId)!.Batting.Hits);
    }

    [Fact]
    public void FromNewSchool_InTenureStatsAreFoldedIntoTheNewAiSchool()
    {
        var generated = SaveTestFixtures.BuildNation();
        var overlay = new NationOverlay();
        var rosters = new NationRosters(new AiRosterDeps());
        var stats = new NationTournamentStats();
        var roster = ManagerRoster();
        var career = new StatBook();
        career.FoldGame(OneBatter(roster[0].Id, hits: 4), managerIsAway: false);
        // 卒業生（名簿に居ない）の成績も学校の通算として残る。
        career.FoldGame(OneBatter(9999, hits: 1), managerIsAway: false);

        var result = SchoolTransfer.Execute(overlay, generated, rosters, new DepartingSchool("新設", 13, null, 50.0),
            roster, generated.InPrefecture(13).First(), Year, FirstManagerId, Seed, stats, career);

        var book = stats.CareerForSchool(result.OldSchoolId);
        Assert.Equal(4, Hits(book, roster[0].Id));
        Assert.Equal(1, Hits(book, 9999));
        // 在任中の帳簿は複写＝この後の自校ストアの変更に引きずられない。
        career.FoldGame(OneBatter(roster[0].Id, hits: 10), managerIsAway: false);
        Assert.Equal(4, Hits(book, roster[0].Id));
    }

    /// <summary>転任先の帳簿に残る、率いている学校の全国側の古い通算はセーブ移行（v37以前）で捨てる。名簿外の自校成績も。</summary>
    [Fact]
    public void LegacySave_DropsStaleCareerBooks()
    {
        var state = new SaveState
        {
            Roster = ManagerRoster(),
            NationOverlay = new NationOverlay { ManagedSchoolId = 7 },
        };
        var rosterId = state.Roster[0].Id;
        state.TournamentStats.FoldSchool(7, OneBatter(3, 2), schoolIsAway: false);   // 率いている学校の古い帳簿
        state.TournamentStats.FoldSchool(8, OneBatter(3, 2), schoolIsAway: false);   // 他校は触らない
        state.Stats.Career.FoldGame(OneBatter(rosterId, 1), managerIsAway: false);
        state.Stats.Career.FoldGame(OneBatter(1000, 1), managerIsAway: false);      // 名簿外（前任校の選手）

        var legacy = SaveCodec.Serialize(state).Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":37");
        Assert.True(SaveCodec.TryDeserialize(legacy, out var s, out var err), err);

        Assert.Null(s.TournamentStats.CareerForSchool(7));
        Assert.NotNull(s.TournamentStats.CareerForSchool(8));
        Assert.Equal(new[] { rosterId }, s.Stats.Career.Players.Keys.ToArray());

        // 現行版のセーブは触らない（往復で変わらない）。
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var current, out var err2), err2);
        Assert.NotNull(current.TournamentStats.CareerForSchool(7));
        Assert.Equal(2, current.Stats.Career.Players.Count);
    }

    [Fact]
    public void SchoolColorOverrides_RoundTripThroughSave_AndOldSavesLoadEmpty()
    {
        var shirt = new RgbColor(0x10, 0x20, 0x30);
        var colors = new TeamColors(shirt, shirt, shirt, shirt, shirt, shirt, shirt, TeamColors.DefaultShoes,
            new TeamStripe(false, shirt, 40.0));
        var state = new SaveState { SchoolColorOverrides = { [4001] = colors } };

        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.Equal(colors.Shirt, s.SchoolColorOverrides[4001].Shirt);

        var v37 = SaveCodec.Serialize(new SaveState())
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":37")
            .Replace(",\"schoolColorOverrides\":[]", "");
        Assert.DoesNotContain("schoolColorOverrides", v37);
        Assert.True(SaveCodec.TryDeserialize(v37, out var old, out var err2), err2);
        Assert.Empty(old.SchoolColorOverrides);
    }
}

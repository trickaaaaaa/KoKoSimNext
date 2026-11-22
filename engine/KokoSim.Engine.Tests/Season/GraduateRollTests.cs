using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using KokoSim.Engine.Tests.Saves;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// issue #1414: 卒業生は選手データ丸ごとでなく「ID・名前・入学/卒業年度」の名前の表で持つ。
/// 年10〜15人ずつ増えるので、本体を持ち続けるとセーブが際限なく重くなる。
/// </summary>
public sealed class GraduateRollTests
{
    private static DevelopingPlayer Player(int id, string name, int grade = 1)
        => new() { Id = id, Name = name, Grade = grade };

    [Fact]
    public void AdvanceYear_Graduates_GoIntoNameTable_WithEnrolledAndGraduatedYears()
    {
        var roster = new List<DevelopingPlayer> { Player(1, "三年甲", 3), Player(2, "二年乙", 2) };
        var rollover = RosterLifecycle.AdvanceYear(roster, 5, new RosterCoefficients(), new Xoshiro256Random(1), nextId: 10);

        var roll = new GraduateRoll();
        roll.AddGraduated(rollover.Graduated, 5);

        var e = Assert.Single(roll.Entries);
        Assert.Equal(new GraduateEntry(1, "三年甲", EnrolledYearIndex: 2, GraduatedYearIndex: 4), e);
        Assert.True(roll.TryGetName(1, out var name));
        Assert.Equal("三年甲", name);
        Assert.False(roll.Contains(2));
    }

    [Fact]
    public void Add_SameId_KeepsOneRow()
    {
        var roll = new GraduateRoll();
        roll.Add(new GraduateEntry(7, "旧名", 1, 3));
        roll.Add(new GraduateEntry(7, "新名", 1, 3));
        Assert.Equal(1, roll.Count);
        Assert.True(roll.TryGetName(7, out var n));
        Assert.Equal("新名", n);
    }

    [Fact]
    public void Save_RoundTrips_NameTable_WithoutPlayerBodies()
    {
        var state = SaveTestFixtures.BuildRichState();
        state.Graduates.Add(new GraduateEntry(101, "卒業太郎", 1, 3));
        state.Graduates.Add(new GraduateEntry(102, "卒業\"次郎\"", 2, 4));

        var json = SaveCodec.Serialize(state);
        Assert.True(SaveCodec.TryDeserialize(json, out var loaded, out var err), err);

        Assert.Equal(state.Graduates.Entries, loaded.Graduates.Entries);
        Assert.Equal(json, SaveCodec.Serialize(loaded));
        Assert.Contains("[101,\"卒業太郎\",1,3]", json);   // 位置配列の1行だけ（選手本体のキーが無い）
    }

    /// <summary>v40 以前: 卒業生（Grade&gt;3）が roster に本体ごと入っている。読み込みで名前の表へ移り、roster から消える。</summary>
    [Fact]
    public void LegacySave_GraduatesInRoster_AreConvertedToNameTable()
    {
        var state = SaveTestFixtures.BuildRichState();
        var activeCount = state.Roster.Count;
        state.Roster.Add(Player(201, "旧卒業生A", grade: 4));
        state.Roster.Add(Player(202, "旧卒業生B", grade: 4));
        var v40 = SaveCodec.Serialize(state)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":40")
            .Replace("\"graduates\":[],", "");
        Assert.DoesNotContain("graduates", v40);

        Assert.True(SaveCodec.TryDeserialize(v40, out var loaded, out var err), err);

        Assert.Equal(activeCount, loaded.Roster.Count);
        Assert.DoesNotContain(loaded.Roster, p => p.Grade > 3);
        Assert.Equal(new[] { "旧卒業生A", "旧卒業生B" }, loaded.Graduates.Entries.Select(e => e.Name));
        Assert.All(loaded.Graduates.Entries, e => Assert.Equal(0, e.GraduatedYearIndex));   // 年度は辿れない＝0
    }

    /// <summary>v37 以前の転任掃除（#1261）が、名前の表へ移す前の卒業生の成績を巻き込んで捨てない。</summary>
    [Fact]
    public void LegacySave_V37_KeepsGraduatesStats()
    {
        var state = SaveTestFixtures.BuildRichState();
        var grad = Player(301, "旧卒業生", grade: 4);
        state.Roster.Add(grad);
        state.Stats.Career.FoldGame(new KokoSim.Engine.Match.Game.GameResult
        {
            AwayName = "A", HomeName = "H", AwayRuns = 0, HomeRuns = 0,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            HomeBatting = new[]
            {
                new KokoSim.Engine.Match.Game.BattingLine(1, KokoSim.Engine.Match.Field.FieldPosition.LeftField,
                    "旧卒業生", 4, 4, 1, 0, 0, 0, 0, 0, 1, SourceId: 301),
            },
        }, managerIsAway: false);
        var v37 = SaveCodec.Serialize(state)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":37")
            .Replace("\"graduates\":[],", "");

        Assert.True(SaveCodec.TryDeserialize(v37, out var loaded, out var err), err);

        Assert.True(loaded.Graduates.Contains(301));
        Assert.NotNull(loaded.Stats.Career.Get(301));
    }

    /// <summary>卒業生の本体が無くなっても、採番の再開値は名前の表の最大IDを超える（IDが重複しない）。</summary>
    [Fact]
    public void Resume_SeesGraduateNameTable()
    {
        var roster = new List<DevelopingPlayer> { Player(1, "a"), Player(2, "b") };
        var roll = new GraduateRoll();
        roll.Add(new GraduateEntry(50, "卒業生", 1, 3));

        Assert.Equal(51, PlayerIdCounter.Resume(0, roster, null, roll));
        Assert.Equal(80, PlayerIdCounter.Resume(80, roster, null, roll));
        Assert.Equal(3, PlayerIdCounter.Resume(0, roster, null, new GraduateRoll()));
    }

    /// <summary>続き番号も成績も持たない旧セーブを読んでも、新入生のIDは卒業生のIDと重ならない。</summary>
    [Fact]
    public void LegacySave_ThenNewYear_NewcomerIdsNeverCollideWithGraduates()
    {
        var state = new SaveState
        {
            Roster = new List<DevelopingPlayer> { Player(1, "在籍", 2) },
        };
        state.Roster.Add(Player(40, "旧卒業生", grade: 4));
        var v40 = SaveCodec.Serialize(state)
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":40")
            .Replace("\"graduates\":[],", "")
            .Replace("\"nextPlayerId\":0,", "");

        Assert.True(SaveCodec.TryDeserialize(v40, out var loaded, out var err), err);
        var next = PlayerIdCounter.Resume(loaded.NextPlayerId, loaded.Roster, loaded.Stats, loaded.Graduates);
        var newcomers = new List<DevelopingPlayer> { Player(0, "新1"), Player(0, "新2") };
        PlayerIdCounter.AssignSequential(newcomers, next);

        Assert.All(newcomers, p => Assert.True(p.Id > 40));
    }
}

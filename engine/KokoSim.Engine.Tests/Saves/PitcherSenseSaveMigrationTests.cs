using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Saves;

/// <summary>
/// 投手センス（issue #1472・v44）のセーブ。v44 は往復で値と見立てが残る。v43 以前（キーが無い）は
/// 生成と同じ分布の形から決定論的に埋める（同じセーブは何度読んでも同じ値）。
/// </summary>
public sealed class PitcherSenseSaveMigrationTests
{
    private static string AsLegacyV43(string json)
    {
        Assert.Contains("\"pitcherSense\":", json);
        json = Regex.Replace(json, "\"pitcherSenseReading\":(null|-?\\d+),", "");
        json = Regex.Replace(json, "\"pitcherSense\":-?\\d+,", "");
        Assert.DoesNotContain("\"pitcherSense", json);
        return json.Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":43");
    }

    private static List<DevelopingPlayer> Roster()
        => ProspectGenerator.Intake(2, new RosterCoefficients(), new Xoshiro256Random(31)).ToList();

    [Fact]
    public void CurrentVersion_IsFortyFour()
        => Assert.Equal(PitcherSenseSaveMigration.IntroducedVersion, SaveCodec.CurrentVersion);

    [Fact]
    public void RoundTrip_KeepsSenseAndReading()
    {
        var roster = Roster();
        roster[0].PitcherSenseReading = 33;
        var json = SaveCodec.Serialize(new SaveState { MasterSeed = 5, Roster = roster });
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);
        Assert.Equal(roster.Select(p => p.PitcherSense), s.Roster.Select(p => p.PitcherSense));
        Assert.Equal(33, s.Roster[0].PitcherSenseReading);
        Assert.Null(s.Roster[1].PitcherSenseReading);
    }

    [Fact]
    public void LegacySave_FillsSenseDeterministically_ShapedLikeGeneration()
    {
        var nation = SaveTestFixtures.BuildNation();
        var ai = AiRosterFactory.Bootstrap(nation.Schools[0], 2, new AiRosterDeps());
        var roster = Roster();
        var json = AsLegacyV43(SaveCodec.Serialize(new SaveState
        {
            MasterSeed = 12345, Roster = roster, NationRosters = new List<AiSchoolRoster> { ai },
        }));

        Assert.True(SaveCodec.TryDeserialize(json, out var a, out var e1), e1);
        Assert.True(SaveCodec.TryDeserialize(json, out var b, out var e2), e2);
        Assert.Equal(a.Roster.Select(p => p.PitcherSense), b.Roster.Select(p => p.PitcherSense));
        Assert.Equal(a.NationRosters[0].Players.Select(p => p.Snapshot.PitcherSense),
            b.NationRosters[0].Players.Select(p => p.Snapshot.PitcherSense));

        // 表示Lv 1〜99・個体差あり・見立ては無い。
        Assert.All(a.Roster, p => Assert.InRange(p.PitcherSense, AbilityScale.DisplayMin, 99));
        Assert.All(a.Roster, p => Assert.Null(p.PitcherSenseReading));
        Assert.True(a.Roster.Select(p => p.PitcherSense).Distinct().Count() > 3);
        Assert.True(a.NationRosters[0].Players.Select(p => p.Snapshot.PitcherSense).Distinct().Count() > 3);

        // 形は生成と同じ: 投手に読める選手ほど高い（平均で）。
        var pit = a.Roster.Where(p => PitcherRoleResolver.LeansPitcher(p)).Select(p => (double)p.PitcherSense).ToList();
        var fld = a.Roster.Where(p => !PitcherRoleResolver.LeansPitcher(p)).Select(p => (double)p.PitcherSense).ToList();
        if (pit.Count > 0 && fld.Count > 0) Assert.True(pit.Average() > fld.Average());
    }

    [Fact]
    public void LegacySave_DifferentMasterSeed_GivesDifferentNoise()
    {
        var roster = Roster();
        string Legacy(ulong seed) => AsLegacyV43(SaveCodec.Serialize(new SaveState { MasterSeed = seed, Roster = roster }));
        Assert.True(SaveCodec.TryDeserialize(Legacy(1), out var a, out _));
        Assert.True(SaveCodec.TryDeserialize(Legacy(2), out var b, out _));
        Assert.NotEqual(a.Roster.Select(p => p.PitcherSense), b.Roster.Select(p => p.PitcherSense));
    }
}

using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Saves;

/// <summary>
/// 表示能力の物差し（issue #1080）のセーブ移行。v41 以前のセーブ（abilityDelta なし＝下げる前）を読むと、
/// 能力・才能上限・精神力・リード・成長推移・AI校の選手の能力が一律 −Δ（下限1）になる＝物理（今の物差し）は保存時と同じ。
/// 弾道・守備位置適性・km/h・目安投球数は物差しの外なので動かない。v42 のセーブは往復で動かない。
/// </summary>
public sealed class AbilityScaleSaveMigrationTests
{
    private const int D = AbilityScale.Delta;

    private static string AsLegacyV41(string json)
    {
        Assert.Contains($"\"abilityDelta\":{D},", json);
        return json.Replace($"\"abilityDelta\":{D},", "")
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":41");
    }

    private static DevelopingPlayer Member()
    {
        var p = new DevelopingPlayer { Id = 1, Name = "部員", Grade = 2, Mental = 60, MentalCap = 80, Lead = 4, LeadCap = 70 };
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, 50 + (int)k); p.SetCap(k, 80 + (int)k % 10); }
        p.SetAptitude(KokoSim.Engine.Match.Field.FieldPosition.Shortstop, 77);
        p.History.Add(new AbilitySample(1, 3, AbilityKinds.All.Select(k => 40 + (int)k).ToArray()));
        return p;
    }

    [Fact]
    public void LegacySave_ShiftsDisplayLevelsDownByDelta()
    {
        var before = Member();
        var json = SaveCodec.Serialize(new SaveState { Roster = new List<DevelopingPlayer> { before } });
        Assert.True(SaveCodec.TryDeserialize(AsLegacyV41(json), out var s, out var err), err);
        var after = Assert.Single(s.Roster);

        foreach (var k in AbilityKinds.All)
        {
            var d = k == AbilityKind.LaunchTendency ? 0 : D;
            Assert.Equal(before.Level(k) - d, after.Level(k));
            Assert.Equal(before.Cap(k) - d, after.Cap(k));
            Assert.Equal(before.History[0].Level(k) - d, after.History[0].Level(k));
        }
        Assert.Equal(60 - D, after.Mental);
        Assert.Equal(80 - D, after.MentalCap);
        Assert.Equal(AbilityScale.DisplayMin, after.Lead);   // 4−7 は表示の下限1で止まる
        Assert.Equal(70 - D, after.LeadCap);
        Assert.Equal(77, after.Aptitude(KokoSim.Engine.Match.Field.FieldPosition.Shortstop));   // 適性は物差しの外
    }

    [Fact]
    public void LegacySave_ShiftsAiSchoolSnapshots_KeepsKmhAndStamina()
    {
        var nation = SaveTestFixtures.BuildNation();
        var deps = new AiRosterDeps();
        var roster = AiRosterFactory.Bootstrap(nation.Schools[0], 2, deps);
        var json = SaveCodec.Serialize(new SaveState { NationRosters = new List<AiSchoolRoster> { roster } });
        Assert.True(SaveCodec.TryDeserialize(AsLegacyV41(json), out var s, out var err), err);

        var loaded = Assert.Single(s.NationRosters);
        for (var i = 0; i < roster.Players.Count; i++)
        {
            var a = roster.Players[i].Snapshot;
            var b = loaded.Players[i].Snapshot;
            Assert.Equal(System.Math.Max(1, a.Contact - D), b.Contact);
            Assert.Equal(System.Math.Max(1, a.Mental - D), b.Mental);
            Assert.Equal(System.Math.Max(1, a.Pitching.Control - D), b.Pitching.Control);
            Assert.Equal(a.LaunchTendency, b.LaunchTendency);
            Assert.Equal(a.Leadership, b.Leadership);
            Assert.Equal(a.Pitching.MaxVelocityKmh, b.Pitching.MaxVelocityKmh, 9);
            Assert.Equal(a.Pitching.StaminaPitches, b.Pitching.StaminaPitches, 9);
        }
    }

    [Fact]
    public void CurrentSave_RoundTripsUnchanged()
    {
        var json = SaveCodec.Serialize(new SaveState { Roster = new List<DevelopingPlayer> { Member() } });
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);
        Assert.Equal(json, SaveCodec.Serialize(s));
    }
}

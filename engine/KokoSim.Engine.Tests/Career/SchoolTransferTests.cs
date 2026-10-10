using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Tests.Saves;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// issue #641: 転任しても部員は学校に残す。旧校は AI校として全国に残り（旧部員もそこに在籍）、
/// 転任先 AI校の部員を自校が引き継ぎ、転任先は全国から外れる（大会に二重出場しない）。
/// </summary>
public sealed class SchoolTransferTests
{
    private const int Year = 4;
    private const ulong Seed = 0xC0FFEEUL;

    private static List<DevelopingPlayer> ManagerRoster()
        => SaveTestFixtures.BuildManagerRoster().Where(p => !p.IsRetired).ToList();

    private static DepartingSchool NewSchool(List<DevelopingPlayer> roster)
        => new("智弁神奈川", 13, null, 55.0);

    private static School TargetIn(KokoSim.Engine.Nation.Nation nation, int pref = 13)
        => nation.InPrefecture(pref).First();

    // ===== 変換 =====

    [Fact]
    public void ToAiPlayer_KeepsIdentityAbilitiesAndAptitudes()
    {
        var dp = ManagerRoster()[3];
        var ai = TransferRosterConversion.ToAiPlayer(dp, Year);

        Assert.Equal(dp.Id, ai.Id);
        Assert.Equal(dp.Id, ai.Snapshot.SourceId);
        Assert.Equal(dp.Name, ai.Snapshot.Name);
        Assert.Equal(dp.Grade, ai.Grade);
        Assert.Equal(Year - (dp.Grade - 1), ai.EnrollmentYearIndex);
        Assert.Equal(dp.Level(AbilityKind.Contact), ai.Snapshot.Contact);
        Assert.Equal(dp.Level(AbilityKind.Fielding), ai.Snapshot.Fielding);   // 適性を掛ける前の地力
        foreach (var pos in AptitudeRoller.AllPositions)
            Assert.Equal(dp.Aptitude(pos), ai.Aptitude(pos));
    }

    /// <summary>
    /// 往復（自校→AI校→自校）で、AI校が持つ情報（氏名・学年・利き・能力・適性・球種）は保たれる。
    /// 持たない情報（才能上限など）は推定で埋まり、上限は現在値＋2 を下回らない。
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesWhatAiPlayersCarry()
    {
        var original = ManagerRoster();
        var ai = TransferRosterConversion.ToAiRoster(900, original, Year);
        var (back, _) = TransferRosterConversion.ToManagerRoster(ai, 1000, Seed);

        foreach (var dp in original)
        {
            var b = back.Single(x => x.Name == dp.Name);
            Assert.Equal(dp.Grade, b.Grade);
            Assert.Equal(dp.Throws, b.Throws);
            Assert.Equal(dp.Bats, b.Bats);
            foreach (var k in new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline,
                         AbilityKind.Speed, AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.Catching,
                         AbilityKind.Control, AbilityKind.PitchRank, AbilityKind.Stuff, AbilityKind.Bunt,
                         AbilityKind.Steal, AbilityKind.Baserunning, AbilityKind.ThrowAccuracy,
                         AbilityKind.LaunchTendency })
                Assert.True(dp.Level(k) == b.Level(k), $"{dp.Name} {k}: {dp.Level(k)} → {b.Level(k)}");
            // 球速・スタミナは km/h・目安球数を経由する（逆変換は四捨五入）。
            Assert.InRange(b.Level(AbilityKind.Velocity), dp.Level(AbilityKind.Velocity) - 1, dp.Level(AbilityKind.Velocity) + 1);
            Assert.InRange(b.Level(AbilityKind.Stamina), dp.Level(AbilityKind.Stamina) - 1, dp.Level(AbilityKind.Stamina) + 1);
            foreach (var pos in AptitudeRoller.AllPositions) Assert.Equal(dp.Aptitude(pos), b.Aptitude(pos));
            Assert.Equal(dp.LearnedPitches.Select(p => p.Type), b.LearnedPitches.Select(p => p.Type));
            foreach (var k in new[] { AbilityKind.Contact, AbilityKind.Control, AbilityKind.Speed })
                Assert.True(b.Cap(k) >= b.Level(k) + 2, $"{b.Name} {k}: cap {b.Cap(k)} < level {b.Level(k)}+2");
        }
    }

    [Fact]
    public void ToManagerRoster_IsDeterministic_AndNumbersFromCounter()
    {
        var ai = TransferRosterConversion.ToAiRoster(900, ManagerRoster(), Year);
        var (a, nextA) = TransferRosterConversion.ToManagerRoster(ai, 500, Seed);
        var (b, _) = TransferRosterConversion.ToManagerRoster(ai, 500, Seed);

        Assert.Equal(Enumerable.Range(500, a.Count), a.Select(p => p.Id));
        Assert.Equal(500 + a.Count, nextA);
        Assert.Equal(a.Select(p => (p.Name, p.GrowthType, p.Personality, p.Cap(AbilityKind.Power))),
            b.Select(p => (p.Name, p.GrowthType, p.Personality, p.Cap(AbilityKind.Power))));
        Assert.Contains(a, p => p.IsCaptain);
        Assert.Contains(a, p => p.UniformNumber > 0);
    }

    /// <summary>3年生は伸びしろが1年ぶんしか残っていない＝推定上限の伸びしろは1年生より小さい（平均で）。</summary>
    [Fact]
    public void InferredCapGap_ShrinksWithGrade()
    {
        var nation = SaveTestFixtures.BuildNation();
        var rosters = new NationRosters(new AiRosterDeps());
        var gaps = new Dictionary<int, List<int>> { [1] = new(), [3] = new() };
        foreach (var school in nation.Schools.Take(40))
        {
            var r = rosters.GetOrBootstrap(school, Year);
            var (players, _) = TransferRosterConversion.ToManagerRoster(r, 1, Seed);
            foreach (var p in players)
                if (gaps.ContainsKey(p.Grade)) gaps[p.Grade].Add(p.Cap(AbilityKind.Contact) - p.Level(AbilityKind.Contact));
        }
        Assert.True(gaps[1].Average() > gaps[3].Average() + 5,
            $"1年 {gaps[1].Average():F1} / 3年 {gaps[3].Average():F1}");
    }

    // ===== 転任の手順 =====

    [Fact]
    public void FromNewSchool_OldSchoolJoinsNation_TargetLeaves_StudentsStay()
    {
        var generated = SaveTestFixtures.BuildNation();
        var overlay = new NationOverlay();
        var rosters = new NationRosters(new AiRosterDeps());
        var roster = ManagerRoster();
        var target = TargetIn(generated);
        var targetCount = rosters.GetOrBootstrap(target, Year).Players.Count;

        var result = SchoolTransfer.Execute(overlay, generated, rosters, NewSchool(roster), roster,
            target, Year, nextPlayerId: 200, Seed);

        var nation = overlay.Apply(generated);
        // 旧校は AI校として1回だけ・転任先は全国に居ない（自校＝専用ID で出場する）。
        Assert.Single(nation.Schools, s => s.Name == "智弁神奈川高校");   // 全国の表記（〜高校）で並ぶ
        Assert.DoesNotContain(nation.Schools, s => s.Id == target.Id);
        Assert.Equal(generated.Schools.Count, nation.Schools.Count);   // 1校増えて1校減る
        var old = nation.Schools.Single(s => s.Id == result.OldSchoolId);
        Assert.Equal(13, old.PrefectureId);
        Assert.DoesNotContain(generated.Schools, s => s.Id == result.OldSchoolId);

        // 旧部員は旧校のロスターに、自校の安定IDのまま在籍する（教え子の成績を引き続ける）。
        Assert.True(rosters.TryGet(result.OldSchoolId, out var oldRoster));
        Assert.Equal(roster.Select(p => p.Id).OrderBy(i => i), oldRoster.Players.Select(p => p.Id).OrderBy(i => i));
        // 転任先の部員は自校へ移り、全国側のロスターからは外れる＝同じ選手が2校に居ない。
        Assert.False(rosters.TryGet(target.Id, out _));
        Assert.Equal(targetCount, result.Roster.Count);
        Assert.All(result.Roster, p => Assert.InRange(p.Id, 200, 200 + targetCount - 1));
        Assert.Equal(200 + targetCount, result.NextPlayerId);
    }

    /// <summary>2回目の転任: 率いていた生成校は全国へ戻り、そのとき率いていた部員がロスターへ書き戻される。</summary>
    [Fact]
    public void SecondTransfer_GeneratedSchoolReturnsToNation()
    {
        var generated = SaveTestFixtures.BuildNation();
        var overlay = new NationOverlay();
        var rosters = new NationRosters(new AiRosterDeps());
        var schools = generated.InPrefecture(13).Take(2).ToList();
        var first = SchoolTransfer.Execute(overlay, generated, rosters, NewSchool(ManagerRoster()), ManagerRoster(),
            schools[0], Year, 200, Seed);

        var second = SchoolTransfer.Execute(overlay, generated, rosters,
            new DepartingSchool(schools[0].Name, 13, schools[0].DistrictId, 61.0), first.Roster,
            schools[1], Year + 1, first.NextPlayerId, Seed);

        var nation = overlay.Apply(generated);
        Assert.Equal(schools[0].Id, second.OldSchoolId);   // 新しい学校を足さず、生成校の ID へ戻す
        Assert.Single(overlay.Added);                       // 追加校は最初の新設校だけ
        Assert.Single(nation.Schools, s => s.Id == schools[0].Id);
        Assert.DoesNotContain(nation.Schools, s => s.Id == schools[1].Id);
        Assert.Equal(61.0, nation.Schools.Single(s => s.Id == schools[0].Id).Strength);
        Assert.True(rosters.TryGet(schools[0].Id, out var back));
        Assert.Equal(first.Roster.Select(p => p.Id).OrderBy(i => i), back.Players.Select(p => p.Id).OrderBy(i => i));
    }

    [Theory]
    [InlineData("葉山南陽高校", "葉山南陽")]
    [InlineData("桜丘", "桜丘")]
    public void SchoolNames_StemAndFull(string full, string stem)
    {
        Assert.Equal(stem, SchoolNames.Stem(full));
        Assert.Equal(full.EndsWith("高校") ? full : full + "高校", SchoolNames.Full(stem));
    }

    /// <summary>追加校の School 実体は差分を当て直しても同じ＝年次進化で動いた強さが初期値へ戻らない。</summary>
    [Fact]
    public void Apply_ReusesAddedSchoolInstance()
    {
        var generated = SaveTestFixtures.BuildNation();
        var overlay = new NationOverlay();
        overlay.Added.Add(new AddedSchool(overlay.NextSchoolId(generated), "新設", 13, null, 40, 15));
        var a = overlay.Apply(generated).Schools.Last();
        a.Strength = 72;
        Assert.Same(a, overlay.Apply(generated).Schools.Last());
        Assert.Equal(72, overlay.Apply(generated).Schools.Last().Strength);
    }

    // ===== セーブ =====

    [Fact]
    public void Overlay_RoundTripsThroughSave()
    {
        var overlay = new NationOverlay { ManagedSchoolId = 17 };
        overlay.Added.Add(new AddedSchool(4001, "智弁神奈川", 13, 2, 55.5, 22.75));
        overlay.Added.Add(new AddedSchool(4002, "桜丘", 26, null, 40, 15));

        var json = SaveCodec.Serialize(new SaveState { NationOverlay = overlay });
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);

        Assert.Equal(17, s.NationOverlay.ManagedSchoolId);
        Assert.Equal(overlay.Added, s.NationOverlay.Added);
    }

    /// <summary>v20 以前のセーブ（差分を持たない）は空の差分で読める＝転任済みでも今の挙動のまま開ける。</summary>
    [Fact]
    public void V20Save_LoadsWithEmptyOverlay()
    {
        var json = SaveCodec.Serialize(SaveTestFixtures.BuildRichState());
        var start = json.IndexOf(",\"nationOverlay\":", System.StringComparison.Ordinal);
        var end = json.IndexOf(",\"incoming\":", start, System.StringComparison.Ordinal);
        var v20 = (json.Substring(0, start) + json.Substring(end))
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":20");
        Assert.DoesNotContain("nationOverlay", v20);

        Assert.True(SaveCodec.TryDeserialize(v20, out var s, out var err), err);
        Assert.True(s.NationOverlay.IsEmpty);
    }
}

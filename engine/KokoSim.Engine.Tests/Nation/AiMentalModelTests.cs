using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の精神力・統率・捕手リード・主将（issue ai-school-mental-fixed-50, 2026-08-18）。
/// 旧実装は全員 Mental=Leadership=Lead=50 固定・主将なし（相手の「精神」ランクが常に D）。
/// </summary>
public sealed class AiMentalModelTests
{
    private static School MakeSchool(int id, double strength, SchoolStyle style = SchoolStyle.Standard)
        => new() { Id = id, Name = $"校{id}", PrefectureId = 13, Strength = strength, Fame = 50, Style = style };

    private static IEnumerable<Player> All(Team t) => StrengthTeamFactory.AllPlayers(t);

    // ===== 使い捨て生成（StrengthTeamFactory.Create / ForSchool） =====

    [Fact]
    public void Create_MentalIsNoLongerFixedAtFifty_AndVariesWithinTheTeam()
    {
        var team = StrengthTeamFactory.Create(55, "校", new Xoshiro256Random(11));
        var mentals = All(team).Select(p => p.Mental).ToList();
        Assert.True(mentals.Distinct().Count() > 5, "精神力が個体差を持たない");
        Assert.True(All(team).Select(p => p.Leadership).Distinct().Count() > 5, "統率傾向が個体差を持たない");
        Assert.True(All(team).Select(p => p.Lead).Distinct().Count() > 5, "捕手リードが個体差を持たない");
        var sd = Math.Sqrt(mentals.Average(m => Math.Pow(m - mentals.Average(), 2)));
        Assert.InRange(sd, 6.0, 18.0);   // 自校と同じ σ≈12（学年アーク込みで少し広がる）
    }

    [Fact]
    public void Create_TierMonotone_MeanMentalRisesWithSchoolStrength()
    {
        // 強さ別に多数の学校を生成し、主力（打順9人）の精神力平均がティアで単調に上がる。
        double MeanFor(double strength)
        {
            var sum = 0.0; var n = 0;
            for (ulong seed = 1; seed <= 40; seed++)
            {
                var team = StrengthTeamFactory.Create(strength, "校", new Xoshiro256Random(seed * 7919UL + (ulong)strength));
                sum += team.BattingOrder.Average(p => p.Mental); n++;
            }
            return sum / n;
        }
        var g = MeanFor(25); var d = MeanFor(55); var b = MeanFor(75); var s = MeanFor(92);
        Assert.True(g < d && d < b && b < s, $"G {g:F1} < D {d:F1} < B {b:F1} < S {s:F1} のはず");
        Assert.True(s - g >= 10.0, $"S−G の差 {s - g:F1} が小さすぎる（校ごとの精神ランクが分かれない）");
    }

    [Fact]
    public void Create_MentalDrawDoesNotConsumeTheMainRng_OtherAbilitiesUnchanged()
    {
        // 精神系の分布だけ変えた係数で生成しても、他の能力ロールは1ビットも動かない（専用 Fork）。
        var a = StrengthTeamFactory.Create(60, "校", new Xoshiro256Random(5), rosterCoeff: new RosterCoefficients());
        var b = StrengthTeamFactory.Create(60, "校", new Xoshiro256Random(5),
            rosterCoeff: new RosterCoefficients { AiMentalStrengthSlope = 0.0, MentalSd = 3.0, AiMentalPerGrade = 0.0 });
        var pa = All(a).ToList(); var pb = All(b).ToList();
        Assert.Equal(pa.Count, pb.Count);
        var mentalDiffers = false;
        for (var i = 0; i < pa.Count; i++)
        {
            Assert.Equal(pa[i].Name, pb[i].Name);
            Assert.Equal(pa[i].Contact, pb[i].Contact);
            Assert.Equal(pa[i].Power, pb[i].Power);
            Assert.Equal(pa[i].Speed, pb[i].Speed);
            Assert.Equal(pa[i].Fielding, pb[i].Fielding);
            Assert.Equal(pa[i].Pitching.MaxVelocityKmh, pb[i].Pitching.MaxVelocityKmh);
            Assert.Equal(pa[i].Pitching.Control, pb[i].Pitching.Control);
            Assert.Equal(pa[i].Personality, pb[i].Personality);
            if (pa[i].Mental != pb[i].Mental) mentalDiffers = true;
        }
        Assert.True(mentalDiffers);
    }

    [Fact]
    public void Create_SetsACaptain_FromTheTopGrade_WhoIsOnTheTeam()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var team = StrengthTeamFactory.Create(60, "校", new Xoshiro256Random(seed));
            var cap = team.Captain;
            Assert.NotNull(cap);
            Assert.Contains(cap, All(team));   // 参照一致（在場判定に使う）
            Assert.Equal(All(team).Max(p => p.Grade), cap!.Grade);
            var power = cap.Leadership * cap.Mental / 100.0;
            Assert.True(All(team).Where(p => p.Grade == cap.Grade).All(p => p.Leadership * p.Mental / 100.0 <= power + 1e-9));
        }
    }

    [Fact]
    public void ForSchool_StyleBonus_LiftsDefensiveMindedAndTotalBaseball()
    {
        double MeanFor(SchoolStyle style)
        {
            var sum = 0.0; var n = 0;
            for (var id = 1; id <= 60; id++)
            {
                var team = StrengthTeamFactory.ForSchool(MakeSchool(id, 60, style), 1);
                sum += All(team).Average(p => p.Mental); n++;
            }
            return sum / n;
        }
        var std = MeanFor(SchoolStyle.Standard);
        var def = MeanFor(SchoolStyle.DefensiveMinded);
        var tot = MeanFor(SchoolStyle.TotalBaseball);
        Assert.True(def > std + 1.5, $"守り勝つ {def:F1} vs 型なし {std:F1}");
        Assert.True(tot > std + 1.5, $"全員野球 {tot:F1} vs 型なし {std:F1}");
    }

    [Fact]
    public void ScoutedProfile_MentalRank_VariesAcrossSchools()
    {
        // 相手校6指標「精神」が全校 D にならない（旧実装は常に D）。
        var tiers = new HashSet<Tier>();
        var rng = new Xoshiro256Random(3);
        for (var id = 1; id <= 200; id++)
        {
            var strength = 20 + rng.NextDouble() * 75;
            var team = StrengthTeamFactory.ForSchool(MakeSchool(id, strength), 1);
            tiers.Add(Tiers.FromStrength(ScoutedTeamProfile.Compute(team, new TeamStrengthCoefficients()).Mental));
        }
        Assert.True(tiers.Count >= 3, $"精神ランクが {string.Join(",", tiers)} しか出ない");
    }

    // ===== 永続ロスター（AiRosterFactory / AiRosterCycle / AiTeamBuilder） =====

    [Fact]
    public void Bootstrap_UpperGrades_CarryTheMentalArc()
    {
        var deps = new AiRosterDeps();
        var roster = AiRosterFactory.Bootstrap(MakeSchool(9, 60), 3, deps);
        double Mean(int grade) => roster.InGrade(grade).Average(p => p.Snapshot.Mental);
        var perGrade = deps.Roster.AiMentalPerGrade;
        // 学年ごとの平均差 ≈ 4（コホートごとの個体差はあるが 40人平均で ±3 程度）
        Assert.InRange(Mean(3) - Mean(1), 2 * perGrade - 5, 2 * perGrade + 5);
        Assert.True(Mean(2) > Mean(1) && Mean(3) > Mean(2), $"1年 {Mean(1):F1} / 2年 {Mean(2):F1} / 3年 {Mean(3):F1}");
    }

    [Fact]
    public void AdvanceOneYear_PromotesMentalByExactlyPerGrade_ForNonPhenoms()
    {
        var deps = new AiRosterDeps
        {
            Persistent = new PersistentRosterCoefficients
            {
                Phenom = new PhenomCoefficients { SpikeRatePerSchoolYear = 0, AllRoundRatePerSchoolYear = 0 },
            },
        };
        var school = MakeSchool(9, 60);
        var roster = AiRosterFactory.Bootstrap(school, 3, deps);
        var before = roster.Players.Where(p => p.Grade < 3).ToDictionary(p => p.Id, p => p.Snapshot.Mental);
        AiRosterCycle.AdvanceOneYear(roster, school, 4, deps);
        var d = (int)Math.Round(deps.Roster.AiMentalPerGrade);
        foreach (var p in roster.Players.Where(p => before.ContainsKey(p.Id)))
            Assert.Equal(Math.Min(99, before[p.Id] + d), p.Snapshot.Mental);
    }

    [Fact]
    public void Bootstrap_MatchesAdvanceOneYear_ForTheSameCohort()
    {
        // ブートストラップで置いた上級生の精神力 ＝ 通常進行で進級させた同じ選手の精神力（同じ量のアーク）。
        var deps = new AiRosterDeps
        {
            Persistent = new PersistentRosterCoefficients
            {
                Phenom = new PhenomCoefficients { SpikeRatePerSchoolYear = 0, AllRoundRatePerSchoolYear = 0 },
            },
        };
        var school = MakeSchool(21, 58);
        var boot = AiRosterFactory.Bootstrap(school, 4, deps);
        var advanced = AiRosterFactory.Bootstrap(school, 3, deps);
        AiRosterCycle.AdvanceOneYear(advanced, school, 4, deps);
        // 年度4の2年生（＝年度3入学）は両者に居る。EnrollmentYearIndex で突き合わせる。
        var a = boot.Players.Where(p => p.EnrollmentYearIndex == 3).OrderBy(p => p.Snapshot.Name).Select(p => (p.Snapshot.Name, p.Snapshot.Mental)).ToList();
        var b = advanced.Players.Where(p => p.EnrollmentYearIndex == 3).OrderBy(p => p.Snapshot.Name).Select(p => (p.Snapshot.Name, p.Snapshot.Mental)).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void AiTeamBuilder_SetsACaptain_OnTheTeam()
    {
        var deps = new AiRosterDeps();
        var school = MakeSchool(12, 62);
        var roster = AiRosterFactory.Bootstrap(school, 2, deps);
        var team = AiTeamBuilder.Build(roster, school, 2, deps);
        Assert.NotNull(team.Captain);
        Assert.Contains(team.Captain!, All(team));
        Assert.Equal(All(team).Max(p => p.Grade), team.Captain!.Grade);
    }

    [Fact]
    public void Backfill_RerollsOnlyWhenEveryoneIsAtTheLegacyFifty()
    {
        var deps = new AiRosterDeps();
        var school = MakeSchool(31, 66, SchoolStyle.DefensiveMinded);
        var fresh = AiRosterFactory.Bootstrap(school, 2, deps);
        Assert.False(AiMentalModel.BackfillIfUnrolled(fresh, school, deps));   // 振ってあるロスターは触らない

        // 旧セーブ相当: 全員 50/50/50 に潰す（能力はそのまま）。
        var legacy = AiRosterFactory.Bootstrap(school, 2, deps);
        var contacts = legacy.Players.ToDictionary(p => p.Id, p => p.Snapshot.Contact);
        foreach (var p in legacy.Players) p.Snapshot = p.Snapshot with { Mental = 50, Leadership = 50, Lead = 50 };
        Assert.True(AiMentalModel.BackfillIfUnrolled(legacy, school, deps));
        Assert.True(legacy.Players.Select(p => p.Snapshot.Mental).Distinct().Count() > 5);
        foreach (var p in legacy.Players) Assert.Equal(contacts[p.Id], p.Snapshot.Contact);   // 能力は不変
        // 決定論: 同じセーブなら同じ値
        var again = AiRosterFactory.Bootstrap(school, 2, deps);
        foreach (var p in again.Players) p.Snapshot = p.Snapshot with { Mental = 50, Leadership = 50, Lead = 50 };
        AiMentalModel.BackfillIfUnrolled(again, school, deps);
        Assert.Equal(legacy.Players.Select(p => p.Snapshot.Mental), again.Players.Select(p => p.Snapshot.Mental));

        // NationRosters 経由（GetOrBootstrap）でも後埋めが一度だけ走る。
        var nr = NationRosters.FromSaved(deps, new[] { legacy });
        foreach (var p in legacy.Players) p.Snapshot = p.Snapshot with { Mental = 50, Leadership = 50, Lead = 50 };
        var got = nr.GetOrBootstrap(school, 2);
        Assert.True(got.Players.Select(p => p.Snapshot.Mental).Distinct().Count() > 5);
    }
}

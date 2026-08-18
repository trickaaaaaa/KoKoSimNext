using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の永続ロスター（#80 / Q19）と怪物（#82 / Q20）の単体テスト。
/// 決定論・3年ID連続・逆算配分整合・引退後ベンチ20成立・怪物出現率と上乗せ例外を検証する。
/// </summary>
public sealed class AiRosterTests
{
    private static School MakeSchool(int id = 7, double strength = 55, double fame = 45)
        => new() { Id = id, Name = "架空東", PrefectureId = 13, Strength = strength, Fame = fame };

    /// <summary>怪物を無効化した deps（逆算整合の分離用）。</summary>
    private static AiRosterDeps NoPhenomDeps() => new()
    {
        Persistent = new PersistentRosterCoefficients
        {
            Phenom = new PhenomCoefficients { SpikeRatePerSchoolYear = 0, AllRoundRatePerSchoolYear = 0 },
        },
    };

    // --- 決定論（不変条件#2） ---

    [Fact]
    public void Bootstrap_IsDeterministic_ForSameSchoolAndYear()
    {
        var deps = new AiRosterDeps();
        var a = AiRosterFactory.Bootstrap(MakeSchool(), 1, deps);
        var b = AiRosterFactory.Bootstrap(MakeSchool(), 1, deps);

        Assert.Equal(a.Players.Count, b.Players.Count);
        foreach (var (pa, pb) in a.Players.Zip(b.Players))
        {
            Assert.Equal(pa.Id, pb.Id);
            Assert.Equal(pa.EnrollmentYearIndex, pb.EnrollmentYearIndex);
            Assert.Equal(pa.Grade, pb.Grade);
            Assert.Equal(pa.Phenom, pb.Phenom);
            Assert.Equal(pa.Snapshot.Contact, pb.Snapshot.Contact);
            Assert.Equal(pa.Snapshot.Power, pb.Snapshot.Power);
            Assert.Equal(pa.Snapshot.Name, pb.Snapshot.Name);
        }
    }

    [Fact]
    public void NewcomerCohort_IsPureFunctionOfSchoolAndEnrollmentYear()
    {
        var deps = new AiRosterDeps();
        var n1 = 1; var n2 = 1;
        var c1 = AiRosterFactory.NewcomerCohort(MakeSchool(id: 42), 5, ref n1, deps);
        var c2 = AiRosterFactory.NewcomerCohort(MakeSchool(id: 42), 5, ref n2, deps);
        Assert.Equal(c1.Count, c2.Count);
        Assert.All(c1.Zip(c2), pair => Assert.Equal(pair.First.Snapshot.Contact, pair.Second.Snapshot.Contact));
        // 別年度・別校は別コホート。
        var n3 = 1;
        var c3 = AiRosterFactory.NewcomerCohort(MakeSchool(id: 42), 6, ref n3, deps);
        Assert.NotEqual(
            c1.Sum(p => p.Snapshot.Contact + p.Snapshot.Power),
            c3.Sum(p => p.Snapshot.Contact + p.Snapshot.Power));
    }

    // --- 3年連続性＋単調に近い成長（#80 完了条件） ---

    [Fact]
    public void Player_PersistsThreeYears_WithSameId_AndGrows()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool();
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);

        // 入学年度=1（＝今年の新入生, Grade1）を1人追跡。選手は配置を持たない（#384）ので
        // 「野手を1人」ではなく素直に先頭を採る（追跡したいのは ID の連続性と成長）。
        var freshman = roster.Players.First(p => p.EnrollmentYearIndex == 1);
        var id = freshman.Id;
        var contactY1 = freshman.Snapshot.Contact;

        AiRosterCycle.AdvanceOneYear(roster, school, 2, deps);   // → Grade2
        var y2 = roster.Players.SingleOrDefault(p => p.Id == id);
        Assert.NotNull(y2);
        Assert.Equal(2, y2!.Grade);

        AiRosterCycle.AdvanceOneYear(roster, school, 3, deps);   // → Grade3
        var y3 = roster.Players.SingleOrDefault(p => p.Id == id);
        Assert.NotNull(y3);
        Assert.Equal(3, y3!.Grade);

        // 単調に近い成長（3年時 ≥ 1年時）。
        Assert.True(y3.Snapshot.Contact >= contactY1);

        // 3年夏の後（翌年へ）引退＝ロスターから消える。
        AiRosterCycle.AdvanceOneYear(roster, school, 4, deps);
        Assert.DoesNotContain(roster.Players, p => p.Id == id);
    }

    // --- 逆算配分整合: チーム総合 ≈ 目標（GrowthTarget＝圧縮後の Strength・既定は恒等＝raw。#80 完了条件・issue prefecture-dominance） ---

    /// <summary>
    /// 逆算配分のターゲットは <see cref="AiRosterCycle.GrowthTarget"/>（＝<c>CompressStrength(raw)</c>）。旧実装は生成の
    /// 圧縮を無視して raw を目標にしていたので、入学中心だけ圧縮しても成長で raw へ戻され、圧縮が実ゲームに届かなかった。
    /// 2026-08-19（設計書21）から圧縮は恒等＝目標は raw そのもの（表示ランクと中身が一致）で、圧縮を入れたときは
    /// 目標もそれに追従することを、圧縮あり（slope 0.55）の deps で別途固定する。
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(55)]
    [InlineData(70)]
    [InlineData(85)]
    public void Bootstrap_TeamOverall_TracksGrowthTarget(double strength)
    {
        foreach (var slope in new[] { 1.0, 0.55 })
        {
            var deps = NoPhenomDeps() with { Roster = new RosterCoefficients { TierCompressionSlope = slope } };
            var school = MakeSchool(strength: strength);
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);

            var team = AiTeamBuilder.Build(roster, school, 1, deps);
            var overall = ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall;
            var target = AiRosterCycle.GrowthTarget(school, deps);

            Assert.Equal(deps.Roster.CompressStrength(strength), target, 9);
            Assert.InRange(overall, target - deps.Persistent.TargetTolerance - 3, target + deps.Persistent.TargetTolerance);
            // 圧縮が効く帯（ピボット40より上）では raw より明確に低い＝raw へ戻す旧挙動の再発検出。恒等では raw に一致する。
            if (slope < 1.0 && strength >= 55) Assert.True(overall < strength - 3, $"raw の強さまで育っている: overall={overall:F1} raw={strength}");
        }
    }

    [Fact]
    public void ReverseSolve_HoldsAcrossTurnover()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool(strength: 60);
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);

        for (var y = 2; y <= 5; y++)
        {
            AiRosterCycle.AdvanceOneYear(roster, school, y, deps);
            var team = AiTeamBuilder.Build(roster, school, y, deps);
            var overall = ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall;
            var target = AiRosterCycle.GrowthTarget(school, deps);
            Assert.InRange(overall, target - deps.Persistent.TargetTolerance - 4,
                target + deps.Persistent.TargetTolerance);
        }
    }

    // --- 代替わり直後（3年引退後）のベンチ20成立（#80 完了条件） ---

    [Fact]
    public void AutumnRoster_AfterGraduation_FieldsTwentyWithPositionCoverage()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool();
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);

        // 秋＝夏後に3年が引退した2学年状態を再現。
        AiRosterCycle.RetireGraduates(roster);

        var team = AiTeamBuilder.Build(roster, school, 1, deps);
        Assert.Equal(9, team.BattingOrder.Count);

        var total = team.BattingOrder.Count + team.Reserves.Count + team.Reserves.Count;
        Assert.True(total >= 20, $"ベンチ入り20人が成立しない (total={total})");

        // 守備固めが全ポジション成立（先発＋控えで8守備位置を網羅）。
        var covered = team.BattingOrder.Concat(team.Reserves).Select(p => p.Position).ToHashSet();
        foreach (var pos in new[] { FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
            FieldPosition.CenterField, FieldPosition.RightField })
            Assert.Contains(pos, covered);
    }

    // --- 怪物（#82 / Q20） ---

    [Fact]
    public void PhenomRoll_MatchesConfiguredRate_WithinBinomialRange()
    {
        var c = new PhenomCoefficients { SpikeRatePerSchoolYear = 0.20, AllRoundRatePerSchoolYear = 0.0 };
        var hits = 0;
        const int n = 20000;
        for (var i = 0; i < n; i++)
        {
            var rng = AiRosterFactory.CohortSeed(i, 1).Fork(0x9E01_0000UL);
            if (PhenomPackages.Roll(c, rng).IsSpike()) hits++;
        }
        // 期待 4000 ± 3σ(≈170)。
        Assert.InRange(hits, 3600, 4400);
    }

    [Fact]
    public void PhenomRoll_IsPureFunctionOfSeed()
    {
        var c = new PhenomCoefficients { SpikeRatePerSchoolYear = 0.5 };
        var a = PhenomPackages.Roll(c, AiRosterFactory.CohortSeed(123, 4).Fork(0x9E01_0000UL));
        var b = PhenomPackages.Roll(c, AiRosterFactory.CohortSeed(123, 4).Fork(0x9E01_0000UL));
        Assert.Equal(a, b);
    }

    [Fact]
    public void PhenomSchool_TeamOverall_ExceedsStrength_OverlayException()
    {
        // 怪物が必ず出る deps（上乗せ例外の確認）。
        var deps = new AiRosterDeps
        {
            Persistent = new PersistentRosterCoefficients
            {
                Phenom = new PhenomCoefficients { SpikeRatePerSchoolYear = 1.0, AllRoundRatePerSchoolYear = 0.0 },
            },
        };
        var school = MakeSchool(strength: 45);   // 弱小・中堅の物語（Q20 §2）
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);

        Assert.Contains(roster.Players, p => p.Phenom.IsPhenom());

        var team = AiTeamBuilder.Build(roster, school, 1, deps);
        var overall = ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall;
        // 逆算配分は怪物を除いて圧縮後の Strength に合わせるので、怪物込みの実チームはそれを上回る。
        var target = AiRosterCycle.GrowthTarget(school, deps);
        Assert.True(overall > target,
            $"怪物在籍校のチーム総合がターゲットを上回らない (overall={overall}, target={target})");
    }

    [Fact]
    public void NonPhenomSchool_TeamOverall_DoesNotOvershoot()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool(strength: 45);
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);
        var team = AiTeamBuilder.Build(roster, school, 1, deps);
        var overall = ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall;
        // 怪物なしは圧縮後の Strength を大きく超えない（上乗せは怪物だけの効果）。
        Assert.True(overall <= AiRosterCycle.GrowthTarget(school, deps) + deps.Persistent.TargetTolerance);
    }
}

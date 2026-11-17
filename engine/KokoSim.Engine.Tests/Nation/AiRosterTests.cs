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

    // --- 年3節目への分割（#843・Phase C）と翌夏に向けた予算（#1058） ---

    /// <summary>
    /// 夏の節目の積み直し（<see cref="AiRosterCycle.PlanNextYear"/>）は能力に触れず予算を積むだけ。
    /// 対象はいまの1・2年（＝翌年度の2・3年）で、学年係数は翌年度の学年（いまの1年＞いまの2年）。
    /// </summary>
    [Fact]
    public void PlanNextYear_SetsBudgetsByNextYearGrade_WithoutTouchingSnapshots()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool(strength: 70);
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);
        AiRosterCycle.RetireGraduates(roster);
        var before = roster.Players.ToDictionary(p => p.Id, p => p.Snapshot);
        var nextId = roster.NextPlayerId;

        AiRosterCycle.PlanNextYear(roster, school, 1, deps);

        Assert.Equal(nextId, roster.NextPlayerId);   // 新入生の先読みは採番を進めない
        Assert.Equal(before.Count, roster.Players.Count);   // 名簿に足さない
        Assert.All(roster.Players, p => Assert.Same(before[p.Id], p.Snapshot));
        var g1 = roster.InGrade(1).Select(p => p.PendingBudget).Distinct().Single();
        var g2 = roster.InGrade(2).Select(p => p.PendingBudget).Distinct().Single();
        Assert.True(g1 > 0, "いまの1年（翌年度の2年）に予算が積まれていない");
        Assert.Equal(g1 * deps.Persistent.SeniorGrowthFactor, g2, 9);   // 翌年度の3年は SeniorGrowthFactor
    }

    /// <summary>
    /// 旧方式の消し残し（4月に積んだその年度の予算＝年度途中の旧セーブ）は、積み直しの前に能力へ乗せる
    /// （捨てると旧セーブのいまの2年が1年分遅れたまま3年になる）。積み直しは乗せた後から逆算するので二重にならない
    /// ＝翌夏のチームは消し残しが無かった場合と同じくターゲットに一致する。3年には予算を積まない。
    /// </summary>
    [Fact]
    public void PlanNextYear_FlushesOldBudgets_ThenReplans_WithoutDoubleGrowth()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool(strength: 70);
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);
        foreach (var p in roster.Players) p.PendingBudget = 6.0;   // 旧方式の消し残し（3年が残ったまま呼ばれても安全）
        var before = roster.Players.ToDictionary(p => p.Id, p => p.Snapshot.Contact);

        AiRosterCycle.PlanNextYear(roster, school, 1, deps);

        Assert.All(roster.Players.Where(p => before[p.Id] <= 93),
            p => Assert.Equal(before[p.Id] + 6, p.Snapshot.Contact));   // 消し残しは能力へ
        Assert.All(roster.InGrade(3), p => Assert.Equal(0, p.PendingBudget));

        AiRosterCycle.RetireGraduates(roster);
        foreach (var node in new[] { AiGrowthNode.Summer, AiGrowthNode.Autumn, AiGrowthNode.Winter })
            AiRosterCycle.ApplyGrowthNode(roster, deps, node);
        AiRosterCycle.BeginYear(roster, school, 2, deps);
        var overall = ScoutedTeamProfile.Compute(AiTeamBuilder.Build(roster, school, 2, deps), deps.TeamStrength).Overall;
        var target = AiRosterCycle.GrowthTarget(school, deps);
        Assert.InRange(overall, target - deps.Persistent.TargetTolerance - 4, target + deps.Persistent.TargetTolerance);
    }

    /// <summary>
    /// 夏の節目→秋→冬の節目で能力が単調に伸び、冬明けで予算を使い切り、翌年度4月（<see cref="AiRosterCycle.BeginYear"/>）の
    /// 後の夏のチーム総合がターゲットに一致する（#843 / #1058 完了条件）。
    /// </summary>
    [Fact]
    public void SummerAutumnWinterNodes_GrowMonotonically_AndNextSummerMeetsTarget()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool(strength: 70);   // 生成中心から伸ばす必要があるが、99クランプに刺さない程度
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);
        AiRosterCycle.RetireGraduates(roster);
        AiRosterCycle.PlanNextYear(roster, school, 1, deps);

        // 予算が付いた選手を1人追跡。個々の能力は BumpTo が全指標へ一律加算のみ＝節目を踏むごとに
        // 単調非減少（99クランプで単能力が動かない回に当たらないよう、合計で見る）。
        var tracked = roster.Players.First(p => p.PendingBudget > 0);
        var id = tracked.Id;
        static int CoreSum(Player s) => s.Contact + s.Power + s.Discipline + s.Speed
            + s.ArmStrength + s.ThrowAccuracy + s.Fielding + s.Catching;
        var afterPlan = CoreSum(roster.Players.Single(p => p.Id == id).Snapshot);

        AiRosterCycle.ApplyGrowthNode(roster, deps, AiGrowthNode.Summer);
        var afterSummer = CoreSum(roster.Players.Single(p => p.Id == id).Snapshot);
        AiRosterCycle.ApplyGrowthNode(roster, deps, AiGrowthNode.Autumn);
        var afterAutumn = CoreSum(roster.Players.Single(p => p.Id == id).Snapshot);
        AiRosterCycle.ApplyGrowthNode(roster, deps, AiGrowthNode.Winter);
        var afterWinter = CoreSum(roster.Players.Single(p => p.Id == id).Snapshot);

        Assert.True(afterSummer > afterPlan, $"夏の節目で伸びていない {afterPlan} -> {afterSummer}");
        Assert.True(afterAutumn >= afterSummer, $"{afterSummer} -> {afterAutumn}");
        Assert.True(afterWinter >= afterAutumn, $"{afterAutumn} -> {afterWinter}");

        // 冬明けで PendingBudget を使い切る（残差0・以後の節目呼び出しは安全な no-op）。
        Assert.All(roster.Players, p => Assert.True(p.PendingBudget <= 1e-9));

        // 翌年度4月の後、夏のチームがターゲットに一致する（参照実装 AdvanceOneYear と同じ許容）。
        AiRosterCycle.BeginYear(roster, school, 2, deps);
        var team = AiTeamBuilder.Build(roster, school, 2, deps);
        var overall = ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall;
        var target = AiRosterCycle.GrowthTarget(school, deps);
        Assert.InRange(overall, target - deps.Persistent.TargetTolerance - 4,
            target + deps.Persistent.TargetTolerance);
    }

    /// <summary>
    /// 夏の節目で先読みした新入生は、翌年度4月に実際に入る新入生と同じ（同じ ID・同じ能力）＝決定論シードで先読みし、
    /// 主乱数も採番も進めていない。同じロスターから2回やれば同じ結果（決定論・不変条件#2）。
    /// </summary>
    [Fact]
    public void PlanNextYear_LookaheadMatchesActualNewcomers_AndIsDeterministic()
    {
        var deps = new AiRosterDeps();
        var school = MakeSchool(strength: 60);
        var a = AiRosterFactory.Bootstrap(school, 1, deps);
        var b = AiRosterFactory.Bootstrap(school, 1, deps);
        AiRosterCycle.RetireGraduates(a);
        AiRosterCycle.RetireGraduates(b);

        var lookId = a.NextPlayerId;
        var lookahead = AiRosterFactory.NewcomerCohort(school, 2, ref lookId, deps);

        AiRosterCycle.ApplySummerNode(a, school, 1, deps);
        AiRosterCycle.ApplySummerNode(b, school, 1, deps);
        Assert.Equal(a.Players.Select(p => p.PendingBudget), b.Players.Select(p => p.PendingBudget));
        Assert.Equal(a.Players.Select(p => p.Snapshot.Contact), b.Players.Select(p => p.Snapshot.Contact));

        foreach (var node in new[] { AiGrowthNode.Autumn, AiGrowthNode.Winter }) AiRosterCycle.ApplyGrowthNode(a, deps, node);
        AiRosterCycle.BeginYear(a, school, 2, deps);
        var actual = a.InGrade(1).ToList();
        Assert.Equal(lookahead.Select(p => p.Id), actual.Select(p => p.Id));
        Assert.Equal(lookahead.Select(p => p.Snapshot.Contact), actual.Select(p => p.Snapshot.Contact));
        Assert.Equal(lookahead.Select(p => p.Snapshot.Power), actual.Select(p => p.Snapshot.Power));
    }

    /// <summary>
    /// 旧セーブ（4月方式＝その年度の予算が <see cref="AiPlayer.PendingBudget"/> に残ったまま・能力は伸びていない）を
    /// 夏の大会中に読み込んでも例外にならず、その夏の節目の積み直しで新方式へ移る＝翌夏は学年が並び、ターゲットに届く。
    /// </summary>
    [Fact]
    public void OldSaveWithAprilStyleBudget_MigratesAtSummerNode()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool(strength: 75);
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);
        // 旧方式の4月: 進級・新入生だけ当て、成長は予算に積んだまま（夏の大会の時点では1年遅れ）。
        roster.RemoveWhere(p => p.Grade >= 3);
        foreach (var p in roster.Players) p.Grade++;
        var nid = roster.NextPlayerId;
        foreach (var n in AiRosterFactory.NewcomerCohort(school, 2, ref nid, deps)) roster.Add(n);
        roster.NextPlayerId = nid;
        foreach (var p in roster.Players.Where(p => p.Grade >= 2)) p.PendingBudget = p.Grade == 3 ? 4.5 : 6.0;

        // その年度の夏の大会後から暦どおりに進める。
        AiRosterCycle.RetireGraduates(roster);
        AiRosterCycle.ApplySummerNode(roster, school, 2, deps);
        AiRosterCycle.ApplyGrowthNode(roster, deps, AiGrowthNode.Autumn);
        AiRosterCycle.ApplyGrowthNode(roster, deps, AiGrowthNode.Winter);
        AiRosterCycle.BeginYear(roster, school, 3, deps);

        double Mean(int g) => roster.InGrade(g).Average(p => p.Snapshot.Contact);
        Assert.True(Mean(1) < Mean(2) && Mean(2) < Mean(3), $"{Mean(1):F1}/{Mean(2):F1}/{Mean(3):F1}");
        var overall = ScoutedTeamProfile.Compute(AiTeamBuilder.Build(roster, school, 3, deps), deps.TeamStrength).Overall;
        var target = AiRosterCycle.GrowthTarget(school, deps);
        Assert.InRange(overall, target - deps.Persistent.TargetTolerance - 4, target + deps.Persistent.TargetTolerance);
    }

    /// <summary>
    /// 夏の節目より後に生成されたロスター（予算なし）や、冬の節目を通らなかった予算の消し残しがあっても、
    /// 4月（<see cref="AiRosterCycle.BeginYear"/>）の補正で夏のチームがターゲットに届く。
    /// </summary>
    [Fact]
    public void BeginYear_CatchesUp_RosterWithoutPlannedBudget()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool(strength: 70);
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);   // 夏の節目の後に生成された＝予算なし
        AiRosterCycle.RetireGraduates(roster);
        AiRosterCycle.BeginYear(roster, school, 2, deps);
        var overall = ScoutedTeamProfile.Compute(AiTeamBuilder.Build(roster, school, 2, deps), deps.TeamStrength).Overall;
        var target = AiRosterCycle.GrowthTarget(school, deps);
        Assert.InRange(overall, target - deps.Persistent.TargetTolerance - 4, target + deps.Persistent.TargetTolerance);
        Assert.All(roster.Players, p => Assert.Equal(0, p.PendingBudget));
    }

    /// <summary>
    /// 旧セーブ（<see cref="AiPlayer.PendingBudget"/>=0のまま）を年度途中（節目）で読み込んでも、
    /// 節目適用は無害な no-op（例外なし・Snapshot不変）になる（issue #843 完了条件）。
    /// </summary>
    [Fact]
    public void ApplyGrowthNode_OldSaveWithZeroPendingBudget_IsSafeNoOp()
    {
        var deps = NoPhenomDeps();
        var school = MakeSchool();
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);
        // Bootstrap 済みロスターは PendingBudget=0（旧セーブと同じ状態）。
        Assert.All(roster.Players, p => Assert.Equal(0, p.PendingBudget));
        var before = roster.Players.Select(p => p.Snapshot.Contact).ToList();

        foreach (var node in new[] { AiGrowthNode.Summer, AiGrowthNode.Autumn, AiGrowthNode.Winter })
            AiRosterCycle.ApplyGrowthNode(roster, deps, node);

        Assert.Equal(before, roster.Players.Select(p => p.Snapshot.Contact).ToList());
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

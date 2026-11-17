using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の永続ロスターを<b>暦どおり</b>に進めたときの学年の水準（issue #1058）。
/// 4月（<see cref="AiRosterCycle.BeginYear"/>）→ 夏の展望 → 3年引退 → 夏の節目 → 秋の展望 → 秋の節目 → 冬の節目、を複数年。
/// 参照実装（<see cref="AiRosterCycle.AdvanceOneYear"/>＝4月に1年分を即時適用）と比べ、夏の大会の時点で
/// 学年の差（1年＜2年＜3年）が同程度に付いていること、チーム総合が逆算配分のターゲットに一致することを固定する。
///
/// 統計の帯は永続ロスターを通らない（帯はロスター生成を測っていない）ので、ここが安全網。
/// 同じ学校群を何本ものテストで使うので、クラス内で1回だけ並列に回して共有する（CLAUDE.md の作法）。
/// </summary>
public sealed class AiRosterCalendarGrowthTests : IClassFixture<AiRosterCalendarGrowthTests.Runs>
{
    private const int SchoolCount = 200;
    private const int LastYear = 11;

    private readonly Runs _runs;
    private readonly ITestOutputHelper _out;

    public AiRosterCalendarGrowthTests(Runs runs, ITestOutputHelper output)
    {
        _runs = runs;
        _out = output;
    }

    /// <summary>1回の通し（全校×全年）の集計。</summary>
    public sealed class Tally
    {
        public readonly double[] SummerContactSum = new double[4];
        public readonly int[] SummerCount = new int[4];
        public readonly double[] AutumnContactSum = new double[4];
        public readonly int[] AutumnCount = new int[4];
        public readonly int[] SummerNotable = new int[4];
        public readonly int[] AutumnNotable = new int[4];
        public readonly int[] SummerNotableStrong = new int[4];
        /// <summary>夏の大会時点の（怪物を除く）チーム総合 − ターゲット。</summary>
        public readonly List<double> SummerOverallMinusTarget = new();
        /// <summary>秋の展望時点の新チーム（1・2年）の平均ミート − 同じ顔ぶれの夏の平均ミート。</summary>
        public readonly List<double> AutumnGainOverSummer = new();

        public double SummerMean(int g) => SummerContactSum[g] / Math.Max(1, SummerCount[g]);
        public double AutumnMean(int g) => AutumnContactSum[g] / Math.Max(1, AutumnCount[g]);

        public static double Share(int[] counts, int g) => (double)counts[g] / Math.Max(1, counts.Sum());

        public void Merge(Tally o)
        {
            for (var g = 0; g < 4; g++)
            {
                SummerContactSum[g] += o.SummerContactSum[g];
                SummerCount[g] += o.SummerCount[g];
                AutumnContactSum[g] += o.AutumnContactSum[g];
                AutumnCount[g] += o.AutumnCount[g];
                SummerNotable[g] += o.SummerNotable[g];
                AutumnNotable[g] += o.AutumnNotable[g];
                SummerNotableStrong[g] += o.SummerNotableStrong[g];
            }
            SummerOverallMinusTarget.AddRange(o.SummerOverallMinusTarget);
            AutumnGainOverSummer.AddRange(o.AutumnGainOverSummer);
        }
    }

    /// <summary>参照実装と暦どおりの2通りを、同じ学校群で1回ずつ回して共有する。</summary>
    public sealed class Runs
    {
        public Tally Reference { get; }
        public Tally Calendar { get; }
        /// <summary>学校の強さが毎年4月に動く（年次進化）場合。</summary>
        public Tally ReferenceEvolving { get; }
        public Tally CalendarEvolving { get; }

        public Runs()
        {
            var deps = new AiRosterDeps();
            Reference = Run(deps, calendar: false, evolve: false);
            Calendar = Run(deps, calendar: true, evolve: false);
            ReferenceEvolving = Run(deps, calendar: false, evolve: true);
            CalendarEvolving = Run(deps, calendar: true, evolve: true);
        }
    }

    /// <summary>
    /// 固定の強さ・名声の学校群（強さ 25〜90 を等間隔・名声は強さに寄せる）。進化（強さの変化）は入れない
    /// ＝学年の水準とターゲットの一致だけを見る。
    /// </summary>
    internal static School MakeSchool(int i)
    {
        var strength = 25.0 + 65.0 * i / (SchoolCount - 1);
        return new School
        {
            Id = 5000 + i, Name = "校" + i, PrefectureId = 13,
            Strength = strength, Fame = 50 + (strength - 50) * 0.8,
        };
    }

    /// <summary>
    /// 年次進化の代わり（4月・<see cref="AiRosterCycle.BeginYear"/> の前に強さを動かす。実ゲームの
    /// <c>EvolveAiSchools</c> → <c>AdvanceAiYear</c> と同じ順）。平均回帰 0.15・揺らぎ σ7.9（coefficients.yaml の
    /// mean_reversion / churn_sd と同じ大きさ）。(校, 年) の決定論シード。
    /// </summary>
    private static void Evolve(School school, int y)
    {
        var rng = new Xoshiro256Random(0x1058_0000UL ^ (ulong)school.Id ^ ((ulong)y * 0x9E37_79B9UL));
        school.Strength = Math.Clamp(school.Strength + 0.15 * (50 - school.Strength) + rng.NextGaussian(0, 7.9), 20, 95);
    }

    internal static Tally Run(AiRosterDeps deps, bool calendar, bool evolve = false)
    {
        var parts = new Tally[SchoolCount];
        Parallel.For(0, SchoolCount, i =>
        {
            var t = new Tally();
            var school = MakeSchool(i);
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);
            for (var y = 2; y <= LastYear; y++)
            {
                if (evolve) Evolve(school, y);
                if (calendar) AiRosterCycle.BeginYear(roster, school, y, deps);
                else AiRosterCycle.AdvanceOneYear(roster, school, y, deps);

                // 夏の展望（夏の県大会）。
                foreach (var p in roster.Players)
                {
                    t.SummerContactSum[p.Grade] += p.Snapshot.Contact;
                    t.SummerCount[p.Grade]++;
                }
                var summerContact = roster.Players.Where(p => p.Grade <= 2)
                    .ToDictionary(p => p.Id, p => p.Snapshot.Contact);
                var summerTeam = AiTeamBuilder.Build(roster, school, y, deps);
                var pitcherLed = TournamentPreviewBuilder.Rate(school) is var r && r.Pitching > r.Batting;
                var sg = PreviewWriter.ChooseNotable(summerTeam, pitcherLed).Player.Grade;
                t.SummerNotable[sg]++;
                if (school.Strength >= 75) t.SummerNotableStrong[sg]++;
                t.SummerOverallMinusTarget.Add(NonPhenomOverall(roster, school, y, deps) - AiRosterCycle.GrowthTarget(school, deps));

                // 夏の大会後: 3年引退 → 夏の節目。
                AiRosterCycle.RetireGraduates(roster);
                if (calendar) AiRosterCycle.ApplySummerNode(roster, school, y, deps);

                // 秋の展望（秋の県大会＝新チーム）。
                foreach (var p in roster.Players)
                {
                    t.AutumnContactSum[p.Grade] += p.Snapshot.Contact;
                    t.AutumnCount[p.Grade]++;
                }
                t.AutumnGainOverSummer.Add(roster.Players.Average(p => p.Snapshot.Contact - summerContact[p.Id]));
                var autumnTeam = AiTeamBuilder.Build(roster, school, y, deps);
                t.AutumnNotable[PreviewWriter.ChooseNotable(autumnTeam, pitcherLed).Player.Grade]++;

                if (calendar)
                {
                    AiRosterCycle.ApplyGrowthNode(roster, deps, AiGrowthNode.Autumn);
                    AiRosterCycle.ApplyGrowthNode(roster, deps, AiGrowthNode.Winter);
                }
            }
            parts[i] = t;
        });
        var all = new Tally();
        foreach (var p in parts) all.Merge(p);   // 校ID順に足す＝並列でも同じ集計
        return all;
    }

    /// <summary>逆算配分の尺度（怪物を除いた在校生で組んだチームの6指標 Overall）。</summary>
    private static double NonPhenomOverall(AiSchoolRoster roster, School school, int y, AiRosterDeps deps)
    {
        var active = roster.Players.Where(p => !p.Phenom.IsPhenom()).ToList();
        var team = AiTeamBuilder.BuildFrom(active, school, y, deps);
        return ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall;
    }

    private void Report()
    {
        foreach (var (name, t) in new[]
        {
            ("参照", _runs.Reference), ("暦どおり", _runs.Calendar),
            ("参照・進化あり", _runs.ReferenceEvolving), ("暦どおり・進化あり", _runs.CalendarEvolving),
        })
        {
            _out.WriteLine($"[{name}] 夏ミート 1年 {t.SummerMean(1):F1} / 2年 {t.SummerMean(2):F1} / 3年 {t.SummerMean(3):F1}" +
                $"｜秋ミート 1年 {t.AutumnMean(1):F1} / 2年 {t.AutumnMean(2):F1}");
            _out.WriteLine($"  注目選手 夏 1/2/3年 = {Tally.Share(t.SummerNotable, 1):P0}/{Tally.Share(t.SummerNotable, 2):P0}/{Tally.Share(t.SummerNotable, 3):P0}" +
                $"（強さ75以上 {Tally.Share(t.SummerNotableStrong, 1):P0}/{Tally.Share(t.SummerNotableStrong, 2):P0}/{Tally.Share(t.SummerNotableStrong, 3):P0}）" +
                $"｜秋 1/2年 = {Tally.Share(t.AutumnNotable, 1):P0}/{Tally.Share(t.AutumnNotable, 2):P0}");
            var d = t.SummerOverallMinusTarget;
            _out.WriteLine($"  夏の総合−ターゲット 平均 {d.Average():+0.00;-0.00} / 絶対値平均 {d.Average(Math.Abs):F2} / 最小 {d.Min():F2} / 最大 {d.Max():F2}" +
                $"｜上振れ(>+{new PersistentRosterCoefficients().TargetTolerance}) {d.Count(x => x > new PersistentRosterCoefficients().TargetTolerance) * 100.0 / d.Count:F1}%" +
                $"｜秋の伸び（同じ顔ぶれ）平均 {t.AutumnGainOverSummer.Average():F2}");
        }
    }

    /// <summary>夏の大会の時点で 1年＜2年＜3年、学年差が参照実装と同程度（issue #1058 完了条件1）。</summary>
    [Fact]
    public void SummerGradeMeans_AreOrdered_AndMatchReferenceGaps()
    {
        Report();
        var c = _runs.Calendar;
        var r = _runs.Reference;
        Assert.True(c.SummerMean(1) < c.SummerMean(2) && c.SummerMean(2) < c.SummerMean(3),
            $"夏の学年別ミート平均が 1年<2年<3年 になっていない: {c.SummerMean(1):F1}/{c.SummerMean(2):F1}/{c.SummerMean(3):F1}");
        Assert.InRange(c.SummerMean(2) - c.SummerMean(1), r.SummerMean(2) - r.SummerMean(1) - 1.5, r.SummerMean(2) - r.SummerMean(1) + 1.5);
        Assert.InRange(c.SummerMean(3) - c.SummerMean(2), r.SummerMean(3) - r.SummerMean(2) - 1.5, r.SummerMean(3) - r.SummerMean(2) + 1.5);
    }

    /// <summary>夏の大会時点のチーム総合が逆算配分のターゲットに一致する（参照実装と同等。完了条件2）。</summary>
    [Fact]
    public void SummerTeamOverall_MatchesGrowthTarget_LikeReference()
    {
        Report();
        var tol = new PersistentRosterCoefficients().TargetTolerance;
        var c = _runs.Calendar.SummerOverallMinusTarget;
        var r = _runs.Reference.SummerOverallMinusTarget;
        // 参照実装と同じ許容（AiRosterTests.ReverseSolve_HoldsAcrossTurnover: −tol−4 〜 +tol）。
        Assert.All(c, d => Assert.InRange(d, -tol - 4, tol));
        // 平均の外れ方も参照実装と同程度（1年遅れなら −数点に偏る）。
        Assert.InRange(c.Average(), r.Average() - 0.5, r.Average() + 0.5);
    }

    /// <summary>秋の大会の新チームは、夏の大会後の節目ぶん夏より伸びている（#843 の意図。完了条件3）。</summary>
    [Fact]
    public void AutumnNewTeam_HasGrownSinceSummer()
    {
        Report();
        var share = new PersistentRosterCoefficients().SummerNodeShare;
        Assert.True(share > 0);
        Assert.True(_runs.Calendar.AutumnGainOverSummer.Average() > 1.0,
            $"秋の新チームが夏から伸びていない: 平均 {_runs.Calendar.AutumnGainOverSummer.Average():F2}");
    }

    /// <summary>夏の展望の注目選手は3年中心（強豪校でも）、秋は2年中心（参照実装と同程度。完了条件4）。</summary>
    [Fact]
    public void NotablePlayers_AreSeniorsInSummer_AndSophomoresInAutumn()
    {
        Report();
        var c = _runs.Calendar;
        var r = _runs.Reference;
        Assert.True(Tally.Share(c.SummerNotable, 3) >= Tally.Share(r.SummerNotable, 3) - 0.10,
            $"夏の注目選手の3年の割合 {Tally.Share(c.SummerNotable, 3):P0}（参照 {Tally.Share(r.SummerNotable, 3):P0}）");
        Assert.True(Tally.Share(c.SummerNotableStrong, 3) >= 0.6,
            $"強豪校の夏の注目選手の3年の割合 {Tally.Share(c.SummerNotableStrong, 3):P0}");
        Assert.True(Tally.Share(c.SummerNotable, 1) <= Tally.Share(r.SummerNotable, 1) + 0.03,
            $"夏の注目選手の1年の割合 {Tally.Share(c.SummerNotable, 1):P0}（参照 {Tally.Share(r.SummerNotable, 1):P0}）");
        Assert.True(Tally.Share(c.AutumnNotable, 2) >= Tally.Share(r.AutumnNotable, 2) - 0.10,
            $"秋の注目選手の2年の割合 {Tally.Share(c.AutumnNotable, 2):P0}（参照 {Tally.Share(r.AutumnNotable, 2):P0}）");
    }

    /// <summary>
    /// 学校の強さが毎年4月に動く場合（年次進化）も、夏の学年の並びと注目選手の学年は参照実装と同程度。
    /// 強くなった学校は4月の補正で追いつく。弱くなった学校は能力を下げないので、夏の節目で積んだ成長のぶん
    /// 参照実装より上振れする（既知・issue #1058 の報告事項）。上振れが今より大きく広がったら気づけるよう幅で固定する。
    /// </summary>
    [Fact]
    public void EvolvingSchools_KeepGradeOrder_AndBoundedOvershoot()
    {
        Report();
        var c = _runs.CalendarEvolving;
        var r = _runs.ReferenceEvolving;
        Assert.True(c.SummerMean(1) < c.SummerMean(2) && c.SummerMean(2) < c.SummerMean(3),
            $"夏の学年別ミート平均が 1年<2年<3年 になっていない: {c.SummerMean(1):F1}/{c.SummerMean(2):F1}/{c.SummerMean(3):F1}");
        Assert.True(c.SummerMean(2) - c.SummerMean(1) >= r.SummerMean(2) - r.SummerMean(1) - 1.5);
        Assert.True(c.SummerMean(3) - c.SummerMean(2) >= r.SummerMean(3) - r.SummerMean(2) - 1.5);
        Assert.True(Tally.Share(c.SummerNotable, 3) >= Tally.Share(r.SummerNotable, 3) - 0.10);
        Assert.True(Tally.Share(c.SummerNotable, 1) <= Tally.Share(r.SummerNotable, 1) + 0.03);
        // 強くなった学校は届く（下振れは参照実装並み）。
        Assert.True(c.SummerOverallMinusTarget.Min() >= r.SummerOverallMinusTarget.Min() - 1.0,
            $"下振れ {c.SummerOverallMinusTarget.Min():F2}（参照 {r.SummerOverallMinusTarget.Min():F2}）");
        // 上振れ（弱くなった学校）は参照実装 +2.5 まで。
        Assert.True(c.SummerOverallMinusTarget.Average() <= r.SummerOverallMinusTarget.Average() + 2.5,
            $"平均 {c.SummerOverallMinusTarget.Average():F2}（参照 {r.SummerOverallMinusTarget.Average():F2}）");
    }
}

using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// Phase 5 DoD: 「修行編→フリー化」のキャリアが自動プレイで再現される（設計書04 §1.3）。
/// A-1 1c: 秋の大会フロー（監督校の秋季→センバツ経路）が CareerYear に記録される（設計書05 §1.5/§4）。
/// </summary>
public sealed class CareerTests
{
    private static readonly SchoolNameVocab Vocab = new();
    private static readonly NationCoefficients NationCoeff = new();
    private static readonly CareerCoefficients CareerCoeff = new();

    private static CareerTimeline RunCareer(int years, ulong seed)
        => CareerEngine.Run(years, new Manager(), Vocab, NationCoeff, CareerCoeff, new Xoshiro256Random(seed));

    private static string DataFile(string name) => Balance.BalanceRegressionTests.FindDataFile(name);
    private static PrefectureTable PrefTable()
        => KokoSim.Config.PrefectureTableLoader.LoadFromFile(DataFile("prefectures.yaml"));
    private static RegionalFormatSet Regionals()
        => KokoSim.Config.RegionalFormatLoader.LoadFromFile(DataFile("pref-formats/regional-tournaments.yaml"));

    [Fact]
    public void Career_ProgressesFromTeacherToFree()
    {
        var t = RunCareer(28, 42);

        // フリー化が起きる。
        Assert.NotNull(t.YearBecameFree);
        Assert.True(t.YearBecameFree > 1, "初年度からフリーではない（修行編がある）");

        // 修行編で複数校を渡り歩く。
        Assert.True(t.SchoolsServed >= 3, $"赴任校数 {t.SchoolsServed}");

        // 最終的にフリー監督。
        Assert.Equal(ManagerStatus.Free, t.Years[^1].Status);
    }

    [Fact]
    public void TeacherPhaseComesBeforeFreePhase_AndFreeIsStable()
    {
        var t = RunCareer(28, 42);
        var freeYear = t.YearBecameFree!.Value;

        // フリー化前は全て教員、フリー化以降は全てフリー（逆行しない）。
        foreach (var y in t.Years)
        {
            if (y.Year < freeYear) Assert.Equal(ManagerStatus.Teacher, y.Status);
            else Assert.Equal(ManagerStatus.Free, y.Status);
        }
    }

    [Fact]
    public void TeacherPhase_HasForcedTransfers()
    {
        var t = RunCareer(28, 42);
        var teacherYears = t.Years.Where(y => y.Status == ManagerStatus.Teacher).ToList();
        // 修行編では強制転任が繰り返される。
        Assert.True(teacherYears.Count(y => y.Transferred) >= 3);
    }

    [Fact]
    public void TeacherPhase_Trust_IsNotFrozen_AndTenuresLastMultipleYears()
    {
        // 回帰: 毎年強制転任＋毎年リセットで信頼度が50に固定される不具合の再発防止。
        var t = RunCareer(28, 42);
        var teacherYears = t.Years.Where(y => y.Status == ManagerStatus.Teacher).ToList();

        // 信頼度が動く（50固定でない）年が存在する＝勝敗が信頼に反映されている。
        Assert.Contains(teacherYears, y => System.Math.Abs(y.Trust - CareerCoeff.TrustReset) > 0.5);

        // 毎年転任ではない＝残留して腰を据える年がある（「3年目に花開く」サイクル）。
        Assert.Contains(teacherYears, y => !y.Transferred);

        // 少なくとも1校で複数年連続在任（残留＝転任が同一県で続く。転任時は別県へ飛ぶため県の連続＝在任継続）。
        var maxTenure = 1; var run = 1;
        for (var i = 1; i < teacherYears.Count; i++)
        {
            run = teacherYears[i].Prefecture == teacherYears[i - 1].Prefecture ? run + 1 : 1;
            maxTenure = System.Math.Max(maxTenure, run);
        }
        Assert.True(maxTenure >= 2, $"教員期に複数年在任が一度も無い（最長 {maxTenure} 年）");
    }

    [Fact]
    public void Coaching_DoesNotSaturateToCapTooEarly()
    {
        // 回帰: 上限近傍の減衰で指導力が十数年で99へ急上昇しない（エンドゲームの単調化防止）。
        var t = RunCareer(16, 42);
        Assert.True(t.Years[^1].AverageCoaching < CareerCoeff.CoachingCap - 5,
            $"16年で上限近傍まで飽和した: {t.Years[^1].AverageCoaching:F1}");
    }

    [Fact]
    public void Coaching_GrowsOverCareer()
    {
        var t = RunCareer(28, 42);
        Assert.True(t.Years[^1].AverageCoaching > t.Years[0].AverageCoaching + 20,
            "指導力がキャリアを通じて成長する");
        // 単調非減少（采配経験で下がらない）。
        for (var i = 1; i < t.Years.Count; i++)
        {
            Assert.True(t.Years[i].AverageCoaching >= t.Years[i - 1].AverageCoaching - 1e-9);
        }
    }

    [Fact]
    public void ReachesKoshien_AfterBuildingProgram()
    {
        var t = RunCareer(28, 42);
        Assert.True(t.KoshienAppearances >= 1, "キャリアを通じて甲子園に出場する");
    }

    [Fact]
    public void Fame_IsCarriedAndGrows()
    {
        var t = RunCareer(28, 42);
        Assert.True(t.Years[^1].Fame > t.Years[0].Fame, "名声が持ち越され成長する");
    }

    [Fact]
    public void Career_IsDeterministic()
    {
        var a = RunCareer(20, 7);
        var b = RunCareer(20, 7);
        Assert.Equal(a.YearBecameFree, b.YearBecameFree);
        Assert.Equal(a.SchoolsServed, b.SchoolsServed);
        for (var i = 0; i < a.Years.Count; i++)
        {
            Assert.Equal(a.Years[i].AverageCoaching, b.Years[i].AverageCoaching, 6);
            Assert.Equal(a.Years[i].Wins, b.Years[i].Wins);
        }
    }

    [Fact]
    public void ManagerAverageCoaching_IsMeanOfThreeFields()
    {
        var m = new Manager { CoachingBatting = 30, CoachingPitching = 60, CoachingDefense = 45 };
        Assert.Equal(45.0, m.AverageCoaching, 6);
    }

    // ===== A-1 1c: 秋の大会フロー配線 =====

    private static CareerTimeline RunWithAutumn(int years, ulong seed)
        => CareerEngine.Run(
            years, new Manager(), Vocab, NationCoeff, CareerCoeff, new Xoshiro256Random(seed),
            growthCoeff: null, prefTable: PrefTable(), regionals: Regionals());

    [Fact]
    public void Autumn_DoesNotPerturbCareerStream()
    {
        // 秋フローは Fork で回すので、既存キャリア（勝数・指導力・転任・フリー化）は秋オフと1ビット一致。
        var baseline = RunCareer(20, 7);
        var withAutumn = RunWithAutumn(20, 7);
        Assert.Equal(baseline.YearBecameFree, withAutumn.YearBecameFree);
        Assert.Equal(baseline.SchoolsServed, withAutumn.SchoolsServed);
        for (var i = 0; i < baseline.Years.Count; i++)
        {
            Assert.Equal(baseline.Years[i].AverageCoaching, withAutumn.Years[i].AverageCoaching, 9);
            Assert.Equal(baseline.Years[i].Wins, withAutumn.Years[i].Wins);
            Assert.Equal(baseline.Years[i].Transferred, withAutumn.Years[i].Transferred);
            // 秋オフでは秋の記録は付かない。
            Assert.False(baseline.Years[i].ReachedSenbatsu);
            Assert.False(baseline.Years[i].ReachedJingu);
        }
    }

    [Fact]
    public void Autumn_ManagerSchool_ReachesSenbatsu_AsProgramGrows()
    {
        // 指導力が育つと監督校が地区を勝ち上がり、翌春センバツに選ばれる年が現れる。
        var t = RunWithAutumn(20, 7);
        Assert.True(t.SenbatsuAppearances >= 1, $"センバツ出場が生涯0回: {t.SenbatsuAppearances}");
        // 神宮出場（地区優勝）はセンバツ出場を含意しないが、記録が立つ年はセンバツ経路と整合。
        Assert.All(t.Years.Where(y => y.ReachedJingu), y => Assert.True(y.ReachedSenbatsu));
        Assert.Equal(t.Years.Count(y => y.ReachedSenbatsu), t.SenbatsuAppearances);
    }

    [Fact]
    public void Autumn_IsDeterministic()
    {
        var a = RunWithAutumn(12, 3);
        var b = RunWithAutumn(12, 3);
        for (var i = 0; i < a.Years.Count; i++)
        {
            Assert.Equal(a.Years[i].ReachedSenbatsu, b.Years[i].ReachedSenbatsu);
            Assert.Equal(a.Years[i].ReachedJingu, b.Years[i].ReachedJingu);
        }
        Assert.Equal(a.SenbatsuAppearances, b.SenbatsuAppearances);
    }
}

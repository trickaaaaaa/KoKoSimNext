using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using KokoSim.Engine.Tests.Balance;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// Phase 3 DoD: 10年自動プレイが無エラー完走し、育成が起きロスターが定常化する。
/// イベント基盤が発火し、シミュレーションは決定論。
/// </summary>
public sealed class SeasonEngineTests
{
    private static SeasonContext LoadContext()
    {
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var eventsPath = BalanceRegressionTests.FindDataFile("events.yaml");
        var b = CoefficientsLoader.LoadFromFile(coeffPath);
        return new SeasonContext
        {
            Training = b.Training,
            Roster = b.Roster,
            Events = EventsLoader.LoadFromFile(eventsPath),
        };
    }

    [Fact]
    public void TenYearCareer_CompletesWithoutError()
    {
        var summary = SeasonEngine.Run(10, LoadContext(), new Xoshiro256Random(42));
        Assert.Equal(10, summary.Years.Count);
    }

    [Fact]
    public void Roster_ReachesSteadyState()
    {
        var summary = SeasonEngine.Run(10, LoadContext(), new Xoshiro256Random(42));
        // 3年生3学年ぶん（各10人前後）で概ね25〜35人に定常化。
        var lastYear = summary.Years[^1];
        Assert.InRange(lastYear.RosterCount, 22, 38);
    }

    [Fact]
    public void GraduatingPlayers_ImprovedOverIntakeBaseline()
    {
        var summary = SeasonEngine.Run(10, LoadContext(), new Xoshiro256Random(42));
        // 新入生の中核初期値は約32。卒業生（3年間育成）はそれを上回るはず。
        var graduatingAverages = summary.Years
            .Where(y => y.GraduatingAvgLevel > 0)
            .Select(y => y.GraduatingAvgLevel)
            .ToList();
        Assert.NotEmpty(graduatingAverages);
        Assert.True(graduatingAverages.Average() > 33.0,
            $"卒業生平均 {graduatingAverages.Average():F1} が初期値32を上回らない");
    }

    [Fact]
    public void HigherBudgetMinutes_RaisesGraduatingAverage()
    {
        // 施設で練習時間(budget)を増やすと、卒業生の平均能力が上がる（要件4の統合検証）。
        var baseCtx = LoadContext();
        var richCtx = baseCtx with { BudgetMinutes = baseCtx.Training.DefaultBudgetMinutes * 3 / 2 }; // 1.5倍

        double GradAvg(SeasonContext ctx) => SeasonEngine.Run(10, ctx, new Xoshiro256Random(42))
            .Years.Where(y => y.GraduatingAvgLevel > 0).Average(y => y.GraduatingAvgLevel);

        Assert.True(GradAvg(richCtx) > GradAvg(baseCtx),
            "練習時間を増やしても卒業生平均が上がらない");
    }

    [Fact]
    public void Fame_Unset_MatchesLegacyBehavior()
    {
        // 不変条件#2: Fame未注入（既定null）は従来のシムと1ビット一致する。
        var ctx = LoadContext();
        var withoutFameField = SeasonEngine.Run(10, ctx, new Xoshiro256Random(42));
        var withNullFame = SeasonEngine.Run(10, ctx with { Fame = null }, new Xoshiro256Random(42));
        for (var i = 0; i < withoutFameField.Years.Count; i++)
        {
            Assert.Equal(withoutFameField.Years[i].RosterCount, withNullFame.Years[i].RosterCount);
            Assert.Equal(withoutFameField.Years[i].AvgLevelRegulars, withNullFame.Years[i].AvgLevelRegulars, 9);
        }
    }

    [Fact]
    public void HigherFame_RaisesGraduatingAverage()
    {
        // 「勝つ→名声→良い新入生」の正フィードバック（issue #127）。名声が高いほど卒業生平均が上がる。
        var baseCtx = LoadContext();
        var famousCtx = baseCtx with { Fame = 100 };

        double GradAvg(SeasonContext ctx) => SeasonEngine.Run(10, ctx, new Xoshiro256Random(42))
            .Years.Where(y => y.GraduatingAvgLevel > 0).Average(y => y.GraduatingAvgLevel);

        Assert.True(GradAvg(famousCtx) > GradAvg(baseCtx with { Fame = 0 }),
            "名声を上げても卒業生平均が上がらない");
    }

    [Fact]
    public void Events_Fire()
    {
        var summary = SeasonEngine.Run(10, LoadContext(), new Xoshiro256Random(42));
        Assert.True(summary.TotalEventsFired > 0);
    }

    [Fact]
    public void Simulation_IsDeterministic()
    {
        var ctx = LoadContext();
        var a = SeasonEngine.Run(10, ctx, new Xoshiro256Random(7));
        var b = SeasonEngine.Run(10, ctx, new Xoshiro256Random(7));
        for (var i = 0; i < a.Years.Count; i++)
        {
            Assert.Equal(a.Years[i].RosterCount, b.Years[i].RosterCount);
            Assert.Equal(a.Years[i].AvgLevelRegulars, b.Years[i].AvgLevelRegulars, 6);
            Assert.Equal(a.Years[i].EventsFired, b.Years[i].EventsFired);
        }
    }

    [Fact]
    public void Run_MatchesGolden_AfterAdvanceYearExtraction()
    {
        // issue #535: 年度替わりロジックを RosterLifecycle.AdvanceYear へ切り出した前後で SeasonEngine.Run の
        // 出力が1ビット一致することを固定する。
        // AdvanceYear が SeasonEngine の消費する乱数列・進級・卒業・新入生を1つでも変えれば、この表が割れる。
        // 採り直し履歴: issue #578 Step 2（球速のバリューを2倍・whiff_intercept で平均三振率は据え置き）で
        // 実戦成長の入力が変わるため再採取した（物理層を意図して変えたときだけ採り直す。#535 の性質は不変）。
        var golden = new (int Year, int Roster, double Reg, double Grad, int Events)[]
        {
            (1, 11, 0, 0, 27),
            (2, 21, 39.554545454545455, 0, 22),
            (3, 30, 38.99047619047619, 0, 31),
            (4, 29, 39.13333333333335, 40.236363636363635, 32),
            (5, 28, 39.10701754385965, 38.42, 29),
            (6, 27, 38.9859649122807, 40.52222222222221, 30),
            (7, 25, 41.115686274509805, 38.6, 31),
            (8, 26, 41.510416666666664, 39.918518518518525, 28),
            (9, 29, 38.55185185185185, 43.00833333333333, 31),
            (10, 31, 37.750793650793646, 40.5, 35),
        };

        var summary = SeasonEngine.Run(10, LoadContext(), new Xoshiro256Random(20260808));
        Assert.Equal(golden.Length, summary.Years.Count);
        for (var i = 0; i < golden.Length; i++)
        {
            var g = golden[i];
            var y = summary.Years[i];
            Assert.Equal(g.Year, y.Year);
            Assert.Equal(g.Roster, y.RosterCount);
            Assert.Equal(g.Reg, y.AvgLevelRegulars, 12);
            Assert.Equal(g.Grad, y.GraduatingAvgLevel, 12);
            Assert.Equal(g.Events, y.EventsFired);
        }
    }

    [Fact]
    public void EventScheduler_LoadsFromYaml()
    {
        var events = EventsLoader.LoadFromFile(BalanceRegressionTests.FindDataFile("events.yaml"));
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.Id == "summer_camp");
    }
}

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
    public void EventScheduler_LoadsFromYaml()
    {
        var events = EventsLoader.LoadFromFile(BalanceRegressionTests.FindDataFile("events.yaml"));
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.Id == "summer_camp");
    }
}

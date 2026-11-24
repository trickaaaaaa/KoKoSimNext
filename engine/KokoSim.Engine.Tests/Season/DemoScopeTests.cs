using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 体験版の範囲＝2年目の夏が全部終わるまで（steam-roadmap 段階4・#1323・2026-11-24 オーナー決定）。
/// 夏が終わった＝第17週（3年引退の週）以降で、夏の大会（甲子園・観るだけの甲子園を含む）がもう動いていない。
/// </summary>
public sealed class DemoScopeTests
{
    private static readonly SeasonCalendar Cal = new();
    private static int Retire => Cal.SummerRetireWeek;

    [Fact]
    public void FirstYear_IsNeverOver()
    {
        for (var w = 0; w < Cal.WeeksPerYear; w++)
            Assert.False(DemoScope.IsOver(1, w, summerOngoing: false, Cal), $"1年目 第{w}週");
    }

    [Fact]
    public void SecondYear_BeforeSummerEnds_IsNotOver()
    {
        Assert.False(DemoScope.IsOver(2, 0, false, Cal));
        Assert.False(DemoScope.IsOver(2, Cal.SummerTournamentStartWeek, true, Cal), "夏の県大会の最中");
        Assert.False(DemoScope.IsOver(2, Retire - 1, false, Cal), "引退の週の前");
    }

    [Fact]
    public void SecondYear_SummerOverAndNoTournamentRunning_IsOver()
    {
        Assert.True(DemoScope.IsOver(2, Retire, false, Cal));
        Assert.True(DemoScope.IsOver(2, Retire + 5, false, Cal));
    }

    [Fact]
    public void SecondYear_KoshienStillRunning_IsNotOverYet()
    {
        // 県大会で深く勝ち進んで負けた年は、観るだけの甲子園が第17週を過ぎても続く。決勝まで見せる
        Assert.False(DemoScope.IsOver(2, Retire + 1, summerOngoing: true, Cal));
    }

    [Fact]
    public void ThirdYearOrLater_IsAlwaysOver()
    {
        Assert.True(DemoScope.IsOver(3, 0, false, Cal));
        Assert.True(DemoScope.IsOver(3, 0, true, Cal));
    }

    [Fact]
    public void StopBeforeAdvance_WhenTheNextWeekWouldBeOver()
    {
        Assert.False(DemoScope.StopBeforeAdvance(2, Retire - 2, false, Cal));
        Assert.True(DemoScope.StopBeforeAdvance(2, Retire - 1, false, Cal), "次の週が引退の週＝夏が終わる");
        Assert.True(DemoScope.StopBeforeAdvance(2, Retire + 3, false, Cal), "もう終わっている（大会明けの時計送りで越えた）");
        Assert.False(DemoScope.StopBeforeAdvance(2, Retire - 1, true, Cal), "甲子園が続いている間は進める");
    }

    [Fact]
    public void StopBeforeAdvance_HandlesTheYearBoundary()
    {
        Assert.False(DemoScope.StopBeforeAdvance(1, Cal.WeeksPerYear - 1, false, Cal), "1年目の最終週→2年目の4月");
    }
}

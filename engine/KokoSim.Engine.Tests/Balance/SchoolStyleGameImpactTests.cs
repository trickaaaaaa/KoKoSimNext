using System.Linq;
using System.Threading.Tasks;
using KokoSim.Balance;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 校風の型（issue #1691）が<b>試合の中身</b>に効くことの統計回帰。6指標（<see cref="TeamStrengthProfile"/>）の
/// 配分が偏るだけでは「ちょっと打てて投手もそこそこ悪い」程度に収まるおそれがある、という issue 本文の懸念に
/// 対する実測（issue 完了条件: 「10点取られても11点取る」チームが出ること）。
///
/// <para>対戦相手は常に同じ強さの型なし（Standard）校。先攻/後攻は試合ごとに入れ替える（どちらかに偏った
/// 先攻有利を測らないため）。尖り方は最大（roll=1.0）＝「強いほうの学校」を測る。</para>
/// </summary>
[Trait("Category", "Heavy")]   // 4校×4000試合（フルゲーム）
public sealed class SchoolStyleGameImpactTests
{
    private readonly ITestOutputHelper _out;
    public SchoolStyleGameImpactTests(ITestOutputHelper output) => _out = output;

    private const int Games = 4000;
    private const double Strength = 60.0;

    private sealed record Outcome(int FocusRuns, int OppRuns, bool FocusWon);

    private static GameContext Ctx() => GameSimulation.BuildContext(null);   // C#既定（不変条件#4）

    /// <summary>
    /// focus 校（<paramref name="focusStyle"/>) vs 同強度の Standard 校を <see cref="Games"/> 試合。
    /// 乱数消費列は <c>GameSimulation.Run</c> と同じ作法（試合ごとに事前 Fork→並列・整数集計は呼び出し側）。
    /// </summary>
    private static Outcome[] Simulate(SchoolStyle? focusStyle, ulong seed)
    {
        var ctx = Ctx();
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[Games];
        for (var i = 0; i < Games; i++) forks[i] = root.Fork((ulong)i);

        var results = new Outcome[Games];
        Parallel.For(0, Games, i =>
        {
            var g = forks[i];
            var focusAway = i % 2 == 0;
            var focusTeam = StrengthTeamFactory.Create(Strength, "Focus", g, style: focusStyle, styleIntensityRoll: 1.0);
            var oppTeam = StrengthTeamFactory.Create(Strength, "Opp", g, style: SchoolStyle.Standard, styleIntensityRoll: 1.0);
            var away = focusAway ? focusTeam : oppTeam;
            var home = focusAway ? oppTeam : focusTeam;
            var r = GameEngine.Play(away, home, ctx, g);
            var focusRuns = focusAway ? r.AwayRuns : r.HomeRuns;
            var oppRuns = focusAway ? r.HomeRuns : r.AwayRuns;
            results[i] = new Outcome(focusRuns, oppRuns, focusRuns > oppRuns);
        });
        return results;
    }

    private static double AvgTotalRuns(Outcome[] o) => o.Average(x => x.FocusRuns + x.OppRuns);

    /// <summary>自軍が <paramref name="threshold"/> 点以上取られた試合に限った勝率（逆転勝ちの頻度）。</summary>
    private static (int Sample, double WinRate) ConcededWinRate(Outcome[] o, int threshold)
    {
        var subset = o.Where(x => x.OppRuns >= threshold).ToList();
        return (subset.Count, subset.Count > 0 ? subset.Count(x => x.FocusWon) / (double)subset.Count : 0.0);
    }

    [Fact]
    public void PowerHitting_TotalRunsAreAtLeast1Point5xStandardBaseline_AndComesBackFromBigDeficitsMoreOften()
    {
        var baseline = Simulate(SchoolStyle.Standard, 0x5C40_0001UL);
        var powerHitting = Simulate(SchoolStyle.PowerHitting, 0x5C40_0002UL);

        var baselineAvg = AvgTotalRuns(baseline);
        var powerHittingAvg = AvgTotalRuns(powerHitting);
        _out.WriteLine($"[PowerHitting] 合計得点/試合: 型なし={baselineAvg:F2} 打高投低={powerHittingAvg:F2}" +
                        $"（比={powerHittingAvg / baselineAvg:F2}倍）");
        Assert.True(powerHittingAvg >= baselineAvg * 1.5,
            $"打高投低の合計得点/試合（{powerHittingAvg:F2}）が型なし（{baselineAvg:F2}）の1.5倍に届かない");

        var (baseSample, baseWinRate) = ConcededWinRate(baseline, 8);
        var (phSample, phWinRate) = ConcededWinRate(powerHitting, 8);
        _out.WriteLine($"[PowerHitting] 8失点以上での勝率: 型なし={baseWinRate:P1}(n={baseSample}) 打高投低={phWinRate:P1}(n={phSample})");
        Assert.True(phSample >= 30, $"8失点以上の試合が少なすぎて検定にならない（n={phSample}）");
        Assert.True(phWinRate > baseWinRate,
            $"打高投低の8失点以上での勝率（{phWinRate:P1}）が型なし（{baseWinRate:P1}）を明らかに上回らない");
    }

    [Fact]
    public void DefensiveMinded_TotalRunsAreClearlyLowerThanStandardBaseline()
    {
        var baseline = Simulate(SchoolStyle.Standard, 0x5C40_0003UL);
        var defensiveMinded = Simulate(SchoolStyle.DefensiveMinded, 0x5C40_0004UL);

        var baselineAvg = AvgTotalRuns(baseline);
        var defensiveAvg = AvgTotalRuns(defensiveMinded);
        _out.WriteLine($"[DefensiveMinded] 合計得点/試合: 型なし={baselineAvg:F2} 投守型={defensiveAvg:F2}" +
                        $"（比={defensiveAvg / baselineAvg:F2}倍）");
        Assert.True(defensiveAvg <= baselineAvg * 0.85,
            $"投守型の合計得点/試合（{defensiveAvg:F2}）が型なし（{baselineAvg:F2}）より明らかに少なくない");
    }
}

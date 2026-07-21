using KokoSim.Balance;
using KokoSim.Config;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 統計回帰テスト（不変条件#5）。data/coefficients.yaml で1万打席を回し、
/// 打率/三振率/四球率/本塁打率が data/balance-targets.yaml の許容帯に収まることを保証する。
/// バランス係数を変更したらこのテストで必ず検証する。
/// </summary>
[Trait("Category", "Heavy")] // 1万打席の統計回帰。日常ループでは --filter "Category!=Heavy" で除外
public sealed class BalanceRegressionTests
{
    [Theory]
    [InlineData(42UL)]
    [InlineData(7UL)]
    [InlineData(2024UL)]
    public void TenThousandAtBats_StayWithinTargetBands(ulong seed)
    {
        var coeffPath = FindDataFile("coefficients.yaml");
        var targets = BalanceTargetsLoader.LoadFromFile(FindDataFile("balance-targets.yaml"));

        var stats = AtBatSimulation.Run(10000, seed, coeffPath);

        Assert.True(targets.BattingAverage.Contains(stats.Average),
            $"AVG {stats.Average:F3} が帯 [{targets.BattingAverage.Min}, {targets.BattingAverage.Max}] 外");
        Assert.True(targets.StrikeoutRate.Contains(stats.StrikeoutRate),
            $"K% {stats.StrikeoutRate:F3} が帯 [{targets.StrikeoutRate.Min}, {targets.StrikeoutRate.Max}] 外");
        Assert.True(targets.WalkRate.Contains(stats.WalkRate),
            $"BB% {stats.WalkRate:F3} が帯 [{targets.WalkRate.Min}, {targets.WalkRate.Max}] 外");
        Assert.True(targets.HomeRunRate.Contains(stats.HomeRunRate),
            $"HR% {stats.HomeRunRate:F4} が帯 [{targets.HomeRunRate.Min}, {targets.HomeRunRate.Max}] 外");

        // 長打の量と配分（Issue #24）。二塁打が構造的に出ない／三塁打が単一距離バケットに
        // 集中する状態への逆戻りをここで止める。
        Assert.True(targets.Slugging.Contains(stats.Slugging),
            $"SLG {stats.Slugging:F3} が帯 [{targets.Slugging.Min}, {targets.Slugging.Max}] 外");
        Assert.True(targets.DoubleRate.Contains(stats.DoubleRate),
            $"2B% {stats.DoubleRate:F4} が帯 [{targets.DoubleRate.Min}, {targets.DoubleRate.Max}] 外");
        Assert.True(targets.TripleRate.Contains(stats.TripleRate),
            $"3B% {stats.TripleRate:F4} が帯 [{targets.TripleRate.Min}, {targets.TripleRate.Max}] 外");
        Assert.True(targets.DoublesPerHomeRun.Contains(stats.DoublesPerHomeRun),
            $"2B÷HR {stats.DoublesPerHomeRun:F2} が帯 [{targets.DoublesPerHomeRun.Min}, {targets.DoublesPerHomeRun.Max}] 外");
    }

    [Fact]
    public void Simulation_IsDeterministic()
    {
        var coeffPath = FindDataFile("coefficients.yaml");
        var a = AtBatSimulation.Run(3000, 99, coeffPath);
        var b = AtBatSimulation.Run(3000, 99, coeffPath);
        Assert.Equal(a.Hits, b.Hits);
        Assert.Equal(a.Strikeouts, b.Strikeouts);
        Assert.Equal(a.HomeRuns, b.HomeRuns);
        Assert.Equal(a.Walks, b.Walks);
    }

    internal static string FindDataFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"data/{fileName} が見つかりません。");
    }
}

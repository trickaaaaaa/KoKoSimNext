using KokoSim.Balance;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 打球の頂点（ApexHeightM）分布の回帰（issue #451・設計書01 §2⑥）。
/// 「フライがフライっぽく見えない＝エンジンが出す頂点が実物より低い」を是正した後、
/// 外野フライ（range≥70m）の頂点 p50 が実物オーダー（20m台）に入ることを固定する。
/// 3Dの絵は engine の ApexHeightM を忠実に写す（表示の誇張は不採用＝issue #451 C不採用）ので、
/// ここが緑＝絵のフライも高く上がる。
/// </summary>
public sealed class BattedBallApexTests
{
    private readonly ITestOutputHelper _out;

    public BattedBallApexTests(ITestOutputHelper output) => _out = output;

    /// <summary>診断プローブの機構検証（軽量・非Heavy）。分位点の単調性・件数の整合を固定する。</summary>
    [Fact]
    public void ApexProfile_ReportsMonotoneQuantiles_AndConsistentCounts()
    {
        var p = AtBatSimulation.RunApexProfile(
            4000, 42UL, BalanceRegressionTests.FindDataFile("coefficients.yaml"));
        _out.WriteLine(p.ToMarkdown());

        // 4分類のいずれも観測され、フライ件数は距離帯の和に一致する。
        var flySum = p.FlyCount(0) + p.FlyCount(1) + p.FlyCount(2);
        Assert.Equal(flySum, p.FlyTotal);
        Assert.True(p.FlyTotal > 0, "フライが1件も観測されなかった");

        // 分位点は単調（p25 ≤ p50 ≤ p75 ≤ p90）で、頂点は妥当な範囲に収まる。
        var p25 = p.ApexQuantile(-1, 0.25);
        var p50 = p.ApexQuantile(-1, 0.50);
        var p75 = p.ApexQuantile(-1, 0.75);
        var p90 = p.ApexQuantile(-1, 0.90);
        Assert.True(p25 <= p50 && p50 <= p75 && p75 <= p90,
            $"頂点分位点が単調でない: p25={p25:F1} p50={p50:F1} p75={p75:F1} p90={p90:F1}");
        Assert.InRange(p50, 0.0, 50.0);
    }

    /// <summary>
    /// 外野フライ（range≥70m）の頂点 p50 が実物オーダー（20m台）に入る（issue #451 完了条件）。
    /// 母集団・解決経路は simulate と同一。C#既定係数（=実ゲーム同値）で回して固定する。
    /// </summary>
    [Theory]
    [Trait("Category", "Heavy")]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void OutfieldFly_ApexP50_ReachesRealisticOrder(ulong seed)
    {
        var p = AtBatSimulation.RunApexProfile(
            30000, seed, BalanceRegressionTests.FindDataFile("coefficients.yaml"));
        _out.WriteLine(p.ToMarkdown());

        const int Outfield = 2; // range≥70m
        var p50 = p.ApexQuantile(Outfield, 0.50);
        Assert.True(p50 >= 20.0,
            $"外野フライの頂点 p50 {p50:F1}m が実物オーダー（20m台）に届いていない（issue #451）");
    }
}

using KokoSim.Balance;
using KokoSim.Config;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 球場①物理層の統計回帰（設計書13-stadiums §4、不変条件#5）。
/// フェンス距離・高さのパラメータ化が本塁打率・長打率・得点に「狭>標準>広」の順で効くことを保証する。
/// AggregateMatch（大会勝敗）は不変なので、既存 balance-targets の帯には影響しない。
/// </summary>
[Trait("Category", "Heavy")] // 数万打席＋数千試合の統計回帰。日常ループでは --filter "Category!=Heavy" で除外
public sealed class StadiumRegressionTests
{
    private static FieldGeometry Stadium(string id)
    {
        var catalog = StadiumCatalogLoader.LoadCatalog(BalanceRegressionTests.FindDataFile("stadiums.yaml"));
        return catalog[id].ToFieldGeometry();
    }

    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void NarrowStandardWide_OrderHomeRunsAndSlugging(ulong seed)
    {
        var coeff = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        const int atBats = 40000;

        var narrow = AtBatSimulation.Run(atBats, seed, coeff, Stadium("municipal_small_01"));
        var standard = AtBatSimulation.Run(atBats, seed, coeff, Stadium("standard_01"));
        var wide = AtBatSimulation.Run(atBats, seed, coeff, Stadium("pref_large_01"));

        // HR% は 狭 > 標準 > 広
        Assert.True(narrow.HomeRunRate > standard.HomeRunRate,
            $"狭 HR% {narrow.HomeRunRate:P2} ≤ 標準 {standard.HomeRunRate:P2}");
        Assert.True(standard.HomeRunRate > wide.HomeRunRate,
            $"標準 HR% {standard.HomeRunRate:P2} ≤ 広 {wide.HomeRunRate:P2}");

        // 長打率(SLG)も同順
        Assert.True(narrow.Slugging > standard.Slugging && standard.Slugging > wide.Slugging,
            $"SLG順序が崩れた: 狭{narrow.Slugging:F3} / 標準{standard.Slugging:F3} / 広{wide.Slugging:F3}");

        // 差は現実的な幅（隣接ティアで HR% 差 ≈±1%、全体でも数%に収まる）
        Assert.InRange(narrow.HomeRunRate - wide.HomeRunRate, 0.005, 0.05);
    }

    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void NarrowStandardWide_OrderRunsPerTeam(ulong seed)
    {
        var coeff = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        const int games = 3000;

        var narrow = GameSimulation.Run(games, seed, coeff, Stadium("municipal_small_01"));
        var standard = GameSimulation.Run(games, seed, coeff, Stadium("standard_01"));
        var wide = GameSimulation.Run(games, seed, coeff, Stadium("pref_large_01"));

        Assert.True(narrow.AverageRunsPerTeam > standard.AverageRunsPerTeam,
            $"狭 得点 {narrow.AverageRunsPerTeam:F2} ≤ 標準 {standard.AverageRunsPerTeam:F2}");
        Assert.True(standard.AverageRunsPerTeam > wide.AverageRunsPerTeam,
            $"標準 得点 {standard.AverageRunsPerTeam:F2} ≤ 広 {wide.AverageRunsPerTeam:F2}");
    }

    [Theory]
    [InlineData(42UL)]
    public void Koshien_ShortWings_ProducesMoreHomeRunsThanWideRegional(ulong seed)
    {
        // 甲子園(95/118)は実寸では両翼が狭い変則球場。地方の広い決勝球場(100/122)より本塁打が出やすい。
        // ※設計書13-stadiums §4 の旧記述「甲子園は本塁打が出にくい」は実寸と食い違う（OPEN-QUESTIONS Q11）。
        var coeff = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        const int atBats = 40000;

        var koshien = AtBatSimulation.Run(atBats, seed, coeff, Stadium("koshien"));
        var wideRegional = AtBatSimulation.Run(atBats, seed, coeff, Stadium("niigata_final"));

        Assert.True(koshien.HomeRunRate > wideRegional.HomeRunRate,
            $"甲子園 HR% {koshien.HomeRunRate:P2} ≤ 地方広め {wideRegional.HomeRunRate:P2}");
    }
}

using KokoSim.Balance;
using KokoSim.Config;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 投打差の格子の統計回帰（issue matchup-gap-saturation-k-rate・不変条件#5）。
///
/// <para><c>atbat_10k</c> は平均同士（N(50,12)）しか測らず、Aランク投手が格下打線から平均24三振・四球ゼロを取る
/// 裾（松井裕樹の22Kが平均になる世界）が帯を全部緑のまま通っていた。ここは Balance の
/// <see cref="AtBatSimulation.RunMatchupGrid"/>（<c>simulate --matchup-grid</c> と同じ経路）で
/// A/S投手 vs E/F打者の K% と、逆側の裾の被打率、互角近傍の不変を固定する。</para>
///
/// <para>校正前（線形ロジット）は P80vsB30 65%・P90vsB30 77%。soft-cap（<c>whiff_matchup_cap</c>=1.5）後は 44%/46%。
/// cap を外したり大きくし過ぎたりすれば赤くなる。</para>
/// </summary>
[Trait("Category", "Heavy")]   // 20マス×6,000打席（約10秒）
public sealed class MatchupGridRegressionTests
{
    private readonly ITestOutputHelper _out;
    public MatchupGridRegressionTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void MatchupGrid_StaysWithinTargetBands(ulong seed)
    {
        var targets = BalanceTargetsLoader.LoadMatchupGridFromFile(
            BalanceRegressionTests.FindDataFile("balance-targets.yaml"));
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");

        var grid = AtBatSimulation.RunMatchupGrid(6000, seed, coeffPath);
        _out.WriteLine(grid.ToMarkdown());

        int P(int level) => System.Array.IndexOf(grid.PitcherLevels, level);
        int B(int level) => System.Array.IndexOf(grid.BatterLevels, level);

        Assert.True(targets.StrikeoutRateP80B30.Contains(grid.StrikeoutRate(P(80), B(30))),
            $"P80 vs B30 K% {grid.StrikeoutRate(P(80), B(30)):P1} が帯外");
        Assert.True(targets.StrikeoutRateP80B45.Contains(grid.StrikeoutRate(P(80), B(45))),
            $"P80 vs B45 K% {grid.StrikeoutRate(P(80), B(45)):P1} が帯外");
        Assert.True(targets.StrikeoutRateP90B30.Contains(grid.StrikeoutRate(P(90), B(30))),
            $"P90 vs B30 K% {grid.StrikeoutRate(P(90), B(30)):P1} が帯外");
        Assert.True(targets.StrikeoutRateP50B45.Contains(grid.StrikeoutRate(P(50), B(45))),
            $"P50 vs B45 K% {grid.StrikeoutRate(P(50), B(45)):P1} が帯外（互角近傍が動いた）");
        Assert.True(targets.AverageP35B75.Contains(grid.Average(P(35), B(75))),
            $"P35 vs B75 被打率 {grid.Average(P(35), B(75)):.000} が帯外");
        Assert.True(targets.AverageP80B30.Contains(grid.Average(P(80), B(30))),
            $"P80 vs B30 被打率 {grid.Average(P(80), B(30)):.000} が帯外");
    }
}

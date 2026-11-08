using KokoSim.Balance;
using Xunit;
using static KokoSim.Balance.DefenseSensitivitySimulation;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 走力・守備の効きの計測コマンド（issue #832）の保証。計測のみで係数・帯は動かさない。
/// 結果の数値そのものは固定せず、決定論・方向・キャッシュ再開だけを守る。
/// </summary>
public sealed class DefenseSensitivityTests
{
    [Fact]
    public void Measure_SameSeed_GivesIdenticalCell()
    {
        var c = new Condition(Attr.Fielding, Scope.All, 20);
        Assert.Equal(Measure(c, 60, 832UL), Measure(c, 60, 832UL));
    }

    [Fact]
    public void Measure_DifferentSeed_Differs()
    {
        var c = new Condition(Attr.Fielding, Scope.All, 20);
        Assert.NotEqual(Measure(c, 60, 832UL), Measure(c, 60, 833UL));
    }

    [Fact]
    public void Fielding_HighLetsFewerRunsThanLow_AndAffectsOnlyTheDefendingSide()
    {
        var low = Measure(new Condition(Attr.Fielding, Scope.All, 20), 300, 832UL);
        var high = Measure(new Condition(Attr.Fielding, Scope.All, 100), 300, 832UL);
        // 全野手の守備20→100は式上かなり大きい（試走で約4点/試合差）。誤差（±0.3点）に埋もれない向きだけ固定する。
        Assert.True(high.RunsPerGame < low.RunsPerGame, $"守備100 {high.RunsPerGame:0.00} / 守備20 {low.RunsPerGame:0.00}");
        Assert.True(high.Babip < low.Babip);
        Assert.True(high.GrounderOutRate > low.GrounderOutRate);
    }

    [Fact]
    public void Contact_HighScoresMoreThanLow()
    {
        var low = Measure(new Condition(Attr.Contact, Scope.All, 20), 300, 832UL);
        var high = Measure(new Condition(Attr.Contact, Scope.All, 80), 300, 832UL);
        Assert.True(high.RunsPerGame > low.RunsPerGame);
    }

    [Fact]
    public void Run_ResumesFromCache_AndReportMatches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"defense-sensitivity-{Guid.NewGuid():N}.tsv");
        try
        {
            // 時間枠0＝最初の条件を測った時点で打ち切られ、途中経過がキャッシュに残る。
            var (partial, remaining) = Run(8, 832UL, null, path, budgetSeconds: 0);
            Assert.Null(partial);
            Assert.True(remaining > 0);
            var linesAfterPartial = File.ReadAllLines(path).Length;
            Assert.Equal(AllConditions().Count - remaining, linesAfterPartial);

            var (resumed, left) = Run(8, 832UL, null, path);
            Assert.Equal(0, left);
            Assert.Equal(AllConditions().Count, File.ReadAllLines(path).Length);

            // 全部キャッシュから読んでも同じレポート＝再開しても結果が変わらない。
            var (again, _) = Run(8, 832UL, null, path);
            Assert.Equal(resumed, again);
            Assert.Contains("守備", resumed!);
            Assert.Contains("能力10ポイントあたり", resumed);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

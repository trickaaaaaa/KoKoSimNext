using System.Globalization;
using KokoSim.Balance;
using KokoSim.Engine.Match.Field;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// ゴロの処理野手分布の回帰（Issue #261）。ゴロの処理野手を「着地点に最も近い内野手」で決めていたため、
/// 本塁前でワンバウンドしたゴロがマウンドの投手に吸われ、ゴロアウトの約7割が投手になっていた。
/// バウンド経路（BouncePath）を辿って実捕球点で処理野手を決めるようにし、投手の割合が現実的な水準
/// （投 ≤ 15%・遊/二 それぞれ ≥ 20%）に収まることを保証する。C#既定係数＝Unity実ゲームと同じ経路で計測。
/// </summary>
public sealed class GrounderFielderDistributionTests
{
    private readonly ITestOutputHelper _out;
    public GrounderFielderDistributionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void GroundOuts_AreNotDominatedByPitcher()
    {
        var tally = AtBatSimulation.RunGroundFielders(atBats: 40000, seed: 42);
        _out.WriteLine(Format(tally));

        Assert.True(tally.GroundOuts > 3000, $"ゴロアウトの母数が少なすぎる（{tally.GroundOuts}）");

        var pitcher = tally.OutFraction(FieldPosition.Pitcher);
        var shortstop = tally.OutFraction(FieldPosition.Shortstop);
        var second = tally.OutFraction(FieldPosition.SecondBase);

        Assert.True(pitcher <= 0.15,
            $"ゴロアウトに占める投手の割合 {pitcher:P1} が高すぎる（着地点決め打ちの回帰）。目安 ≤ 15%");
        Assert.True(shortstop >= 0.20,
            $"ゴロアウトに占める遊撃の割合 {shortstop:P1} が低すぎる。目安 ≥ 20%");
        Assert.True(second >= 0.20,
            $"ゴロアウトに占める二塁の割合 {second:P1} が低すぎる。目安 ≥ 20%");
    }

    private static string Format(AtBatSimulation.GroundFielderTally t)
    {
        var c = CultureInfo.InvariantCulture;
        string Row(string label, FieldPosition p) =>
            string.Format(c, "| {0} | {1} ({2:P1}) | {3} ({4:P1}) |",
                label, t.All[(int)p], t.AllFraction(p), t.Outs[(int)p], t.OutFraction(p));
        return string.Join("\n", new[]
        {
            $"ゴロ総数 {t.GroundBalls} / ゴロアウト {t.GroundOuts}",
            "| 守備 | ゴロ全体 | ゴロアウト |",
            "|---|---|---|",
            Row("投", FieldPosition.Pitcher),
            Row("遊", FieldPosition.Shortstop),
            Row("二", FieldPosition.SecondBase),
            Row("三", FieldPosition.ThirdBase),
            Row("一", FieldPosition.FirstBase),
            Row("捕", FieldPosition.Catcher),
            Row("左", FieldPosition.LeftField),
            Row("中", FieldPosition.CenterField),
            Row("右", FieldPosition.RightField),
        });
    }
}

using System.Globalization;
using KokoSim.Balance;
using KokoSim.Engine.Match.Field;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 試合シム経路（<see cref="GameSimulation"/>）のゴロ処理野手分布の回帰
/// （issue game-sim-all-right-handed-batters）。
///
/// <para><see cref="AtBatSimulation.RunGroundFielders"/> は issue #262 の <c>SampleBats</c> 導入で
/// 左右混在の母集団に直っていたが、<see cref="GameSimulation.GenerateTeam"/> 側は
/// <c>Bats</c> を設定しておらず<b>全員右打ち</b>のままだった。打球方向は打席の左右で反転するため、
/// 全員右打ちだとゴロが三塁・遊撃側へ極端に偏る（修正前実測: 投4.6%・一2.3%・二14.4%・三41.9%・遊36.7%）。
/// <c>games_10k</c> 系の帯はこの偏った母集団で校正されていた。</para>
///
/// <para>この回帰は打席シム側（<see cref="GrounderFielderDistributionTests"/>）とは別に、
/// 実ゲームと同じ <see cref="GameSimulation.Run"/> 経路で内野ゴロの分布を測る。</para>
/// </summary>
[Trait("Category", "Heavy")]
public sealed class GameSimGroundFielderDistributionTests
{
    private readonly ITestOutputHelper _out;
    public GameSimGroundFielderDistributionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void GroundChances_AreNotDominatedByThirdBaseAndShortstop()
    {
        var coeffPath = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var s = GameSimulation.Run(3000, 20260827UL, coeffPath);
        _out.WriteLine(Format(s));

        long ch(FieldPosition p) => s.GroundChancesByPos[(int)p];
        var total = ch(FieldPosition.Pitcher) + ch(FieldPosition.FirstBase) + ch(FieldPosition.SecondBase)
            + ch(FieldPosition.ThirdBase) + ch(FieldPosition.Shortstop);
        Assert.True(total > 20000, $"内野ゴロ機会の母数が少なすぎる（{total}）");

        double Share(FieldPosition p) => (double)ch(p) / total;

        // 全員右打ち（修正前）は三塁41.9%・遊撃36.7%・一塁2.3%まで偏っていた。
        // 左右混在（AtBatSimulation.RunGroundFielders 実測: 投4.1%・一20.6%・二24.1%・三21.4%・遊25.5%）
        // に対して緩めの帯を取る。
        Assert.True(Share(FieldPosition.ThirdBase) <= 0.30,
            $"三塁のゴロ機会シェア {Share(FieldPosition.ThirdBase):P1} が高すぎる（右打者偏重の回帰）");
        Assert.True(Share(FieldPosition.FirstBase) >= 0.10,
            $"一塁のゴロ機会シェア {Share(FieldPosition.FirstBase):P1} が低すぎる（左打者不在の回帰）");
        Assert.True(Share(FieldPosition.SecondBase) >= 0.15,
            $"二塁のゴロ機会シェア {Share(FieldPosition.SecondBase):P1} が低すぎる");
        Assert.True(Share(FieldPosition.Shortstop) >= 0.15,
            $"遊撃のゴロ機会シェア {Share(FieldPosition.Shortstop):P1} が低すぎる");
    }

    private static string Format(GameSimulation.Stats s)
    {
        var c = CultureInfo.InvariantCulture;
        string Row(string label, FieldPosition p) =>
            string.Format(c, "| {0} | {1} |", label, s.GroundChancesByPos[(int)p]);
        return string.Join("\n", new[]
        {
            "| 守備 | ゴロ機会 |",
            "|---|---|",
            Row("投", FieldPosition.Pitcher),
            Row("一", FieldPosition.FirstBase),
            Row("二", FieldPosition.SecondBase),
            Row("三", FieldPosition.ThirdBase),
            Row("遊", FieldPosition.Shortstop),
        });
    }
}

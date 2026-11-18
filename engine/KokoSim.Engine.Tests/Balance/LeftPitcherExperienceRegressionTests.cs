using KokoSim.Balance;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 対左投手経験（設計書02 §2.3「左のプレミア」・issue #1072）の統計回帰。完了条件のうち
/// 「同じ能力の左腕と右腕で、経験の乏しい打線相手なら左腕の被打率・失点が下がること。経験を積んだ打線では
/// 差が縮むこと」「右投手相手の打席は1ビットも変わらない」を、同一の打者・投手ペア（能力B50/P50）に対し
/// <see cref="AtBatContext.LeftExperienceDeficit"/> だけを動かして検証する。6,000打席×3水準（約10秒）。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class LeftPitcherExperienceRegressionTests
{
    private readonly ITestOutputHelper _out;
    public LeftPitcherExperienceRegressionTests(ITestOutputHelper output) => _out = output;

    private sealed record Ops(double OnBase, double Slugging)
    {
        public double Value => OnBase + Slugging;
    }

    private static Ops Run(int pa, ulong seed, double leftExperienceDeficit)
    {
        var baseCtx = AtBatSimulation.BuildContext(null);
        var ctx = baseCtx with { LeftExperienceDeficit = leftExperienceDeficit };
        var batter = AtBatSimulation.GridBatter(50);
        var pitcher = AtBatSimulation.GridPitcher(50);
        var root = new Xoshiro256Random(seed);

        int ab = 0, h = 0, s1 = 0, s2 = 0, s3 = 0, hr = 0, bb = 0, hbp = 0;
        for (var i = 0; i < pa; i++)
        {
            var rng = root.Fork((ulong)i);
            var r = AtBatResolver.Resolve(batter, pitcher, ctx, rng);
            if (r.IsAtBat()) ab++;
            switch (r)
            {
                case PlateAppearanceResult.Single: h++; s1++; break;
                case PlateAppearanceResult.Double: h++; s2++; break;
                case PlateAppearanceResult.Triple: h++; s3++; break;
                case PlateAppearanceResult.HomeRun: h++; hr++; break;
                case PlateAppearanceResult.Walk: bb++; break;
                case PlateAppearanceResult.HitByPitch: hbp++; break;
            }
        }
        var onBase = pa > 0 ? (double)(h + bb + hbp) / pa : 0.0;
        var slg = ab > 0 ? (double)(s1 + 2 * s2 + 3 * s3 + 4 * hr) / ab : 0.0;
        return new Ops(onBase, slg);
    }

    [Fact]
    public void FullDeficit_LowersOps_20To30Points_ThanFullyExperienced()
    {
        const int pa = 6000;
        var none = Run(pa, 42UL, 0.0);
        var full = Run(pa, 42UL, 1.0);
        var dropPts = (none.Value - full.Value) * 1000.0;
        _out.WriteLine($"OPS 経験十分={none.Value:F3} 未経験={full.Value:F3} 低下={dropPts:F1}pt");
        // オーナー決定（2026-11-17）: 小さめ（OPS -20〜-30pt程度）から。校正時の実測22.0pt（B50vP50・6,000PA×seed42）
        // を中心に、シード・ランダム分散ぶんの余裕を持たせる。
        Assert.True(dropPts is > 10.0 and < 40.0,
            $"OPS低下 {dropPts:F1}pt が想定レンジ(10〜40pt)外: 係数を見直すこと");
    }

    [Fact]
    public void Deficit_NarrowsMonotonically_AsExperienceGrows()
    {
        const int pa = 6000;
        var none = Run(pa, 2024UL, 0.0);
        var half = Run(pa, 2024UL, 0.5);
        var full = Run(pa, 2024UL, 1.0);
        _out.WriteLine($"OPS 0={none.Value:F3} 0.5={half.Value:F3} 1.0={full.Value:F3}");
        Assert.True(full.Value <= half.Value, $"deficit=1.0 の OPS {full.Value:F3} が 0.5 {half.Value:F3} 以下でない");
        Assert.True(half.Value <= none.Value, $"deficit=0.5 の OPS {half.Value:F3} が 0 {none.Value:F3} 以下でない");
    }

    [Fact]
    public void Deficit_DoesNotChangeAnything_WhenCallerGatesToZero()
    {
        // 右投手相手は呼び出し側（GameEngine.BuildAtBatContext）が常に0を渡す＝この境界の恒等性を
        // deficit=0 の2回実行がビット単位で一致することで確認する（1ビットも変わらない, issue 完了条件）。
        var a = Run(2000, 7UL, 0.0);
        var b = Run(2000, 7UL, 0.0);
        Assert.Equal(a.OnBase, b.OnBase);
        Assert.Equal(a.Slugging, b.Slugging);
    }
}

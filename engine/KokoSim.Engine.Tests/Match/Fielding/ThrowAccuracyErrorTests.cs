using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 送球精度（ThrowAccuracy）の守備解決への接続（Issue #37, design-14 P1-6b）。
/// 内野ゴロ→一塁の送球エラー（2段階目）が精度連動＝ThrowErrorBaseProb=0 で現行と恒等、
/// 正値では精度が低いほど失策（＝ReachedOnError）が増えることを確認する。
/// </summary>
public sealed class ThrowAccuracyErrorTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    // 内野ゴロ（遊撃へ強めの当たり）。一塁送球が確実に間に合ってアウトになる＝送球エラーロールが載る打球
    // （Issue #261 で処理点を実捕球点へ移し内野処理オーバーヘッドを再校正したため、緩いゴロは内野安打になり
    // 送球ロールに載らない。ここでは確実にアウトになる強いゴロを使う）。
    private static BattedBallResult Grounder(FieldingCoefficients coeff, int throwAccuracy, ulong seed)
        => FieldingResolver.Resolve(
            new BattedBall { ExitVelocityMps = 34.0, LaunchAngleDeg = -4.0, BearingDeg = -12.0 },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { ThrowAccuracy = throwAccuracy }),
            coeff, new Xoshiro256Random(seed));

    private static double ErrorRate(FieldingCoefficients coeff, int throwAccuracy, int trials = 6000)
    {
        var errors = 0;
        for (ulong seed = 1; seed <= (ulong)trials; seed++)
            if (Grounder(coeff, throwAccuracy, seed) == BattedBallResult.Error) errors++;
        return (double)errors / trials;
    }

    [Fact]
    public void ThrowErrorDisabled_IsIdenticalToCatchOnly()
    {
        // base=0 では送球ロールを一切引かない＝ThrowAccuracy を変えても結果が1件も変わらない（恒等の担保）。
        // 既定は issue #169 で正値化されたため、ここは明示的に 0 を指定して恒等性を検証する。
        var coeff = new FieldingCoefficients { ThrowErrorBaseProb = 0 };
        for (ulong seed = 1; seed <= 400; seed++)
        {
            Assert.Equal(Grounder(coeff, 10, seed), Grounder(coeff, 90, seed));
        }
    }

    [Fact]
    public void ThrowError_LowerAccuracyErrsMore()
    {
        // 送球ロール有効時、精度が低いほど失策率が高い（単調）。捕球ロールは Catching50 固定で共通。
        var coeff = new FieldingCoefficients { ThrowErrorBaseProb = 0.10, ThrowErrorAccuracySlope = 0.004 };
        var poor = ErrorRate(coeff, throwAccuracy: 10);
        var avg = ErrorRate(coeff, throwAccuracy: 50);
        var elite = ErrorRate(coeff, throwAccuracy: 95);

        Assert.True(poor > avg, $"精度低{poor:F3} が平均{avg:F3} 以下");
        Assert.True(avg > elite, $"平均{avg:F3} が精度高{elite:F3} 以下");
    }

    [Fact]
    public void ThrowError_AtAccuracyFifty_AddsBaselineErrorAboveCatchOnly()
    {
        // Ac=50 でも送球ロール（base>0）ぶん失策は増える（捕球のみ<2段階）。恒等は base=0 側で担保。
        var catchOnly = new FieldingCoefficients { ThrowErrorBaseProb = 0 };
        var twoStage = new FieldingCoefficients { ThrowErrorBaseProb = 0.10 };
        Assert.True(ErrorRate(twoStage, 50) > ErrorRate(catchOnly, 50));
    }

    // --- 送球逸れの回収レース（design-12 §3.8, 2026-08-30）: 悪送球が確定しても「毎回即失策」にはしない ---

    [Fact]
    public void WildThrowRecovery_TinyScatter_LowerErrorRate_ThanHugeScatter()
    {
        // 逸れ幅が小さいほど一塁手/右翼手の拾い直しが間に合いやすく、悪送球が確定していても
        // 最終的な失策(ReachedOnError)率は下がる（拾い直しでアウトのまま守れるケースが増える）。
        // ErrorMaxProb=1.0＝悪送球ロール自体の天井(既定0.30)を外し、回収レースの効果だけを測る。
        var tiny = new FieldingCoefficients { ThrowErrorBaseProb = 1.0, ErrorMaxProb = 1.0, WildThrowScatterSigmaM = 0.05 };
        var huge = new FieldingCoefficients { ThrowErrorBaseProb = 1.0, ErrorMaxProb = 1.0, WildThrowScatterSigmaM = 500.0 };
        var tinyRate = ErrorRate(tiny, throwAccuracy: 50);
        var hugeRate = ErrorRate(huge, throwAccuracy: 50);
        Assert.True(tinyRate < hugeRate, $"逸れ幅が小さいのに失策率が下がっていない: tiny={tinyRate:F3} huge={hugeRate:F3}");
    }

    [Fact]
    public void WildThrowRecovery_HugeScatter_AlmostAlwaysErrors()
    {
        // 逸れ幅が極端に大きければ拾い直しは絶対に間に合わない＝従来の二値モデル（悪送球=即失策）へ縮退する。
        // ErrorMaxProb=1.0＝悪送球ロール自体の天井(既定0.30)を外し、回収レースの効果だけを測る。
        var coeff = new FieldingCoefficients { ThrowErrorBaseProb = 1.0, ErrorMaxProb = 1.0, WildThrowScatterSigmaM = 20000.0 };
        var rate = ErrorRate(coeff, throwAccuracy: 50, trials: 2000);
        Assert.True(rate > 0.80, $"逸れ幅が極端に大きいのに失策率が下がりすぎている: {rate:F3}");
    }
}

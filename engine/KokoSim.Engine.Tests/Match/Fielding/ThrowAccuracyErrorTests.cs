using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 送球精度（ThrowAccuracy）の守備解決への接続（Issue #37）。内野ゴロ→一塁送球のみ、捕球ロール
/// （Catching）→送球ロール（ThrowAccuracy）の2段階で失策確率を按分する。ThrowAccuracy50で恒等
/// （帯不変）／ThrowAccuracy低下で単調に悪送球率が上昇することを確認する。フライ捕球の落球は
/// 送球という事象が無いため対象外＝ThrowAccuracyの影響を受けない。
/// </summary>
public sealed class ThrowAccuracyErrorTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    // 内野ゴロ（ExitVelocityMps=32・LaunchAngleDeg=-3）: AtBatPipelineTests の
    // FieldingResolver_HardGrounder_IsNotHomeRunOrFoul と同一条件で、内野ゴロアウトの分岐を安定して踏む。
    private static BattedBall Grounder() => new() { ExitVelocityMps = 32, LaunchAngleDeg = -3, BearingDeg = 5 };

    private static double GroundBallErrorRate(int throwAccuracy, FieldingCoefficients coeff, int trials = 2000)
    {
        var fielders = Field.StandardAlignment(new FielderAttributes { Catching = 50, ThrowAccuracy = throwAccuracy });
        var errors = 0;
        for (ulong seed = 0; seed < (ulong)trials; seed++)
        {
            var res = FieldingResolver.Resolve(
                Grounder(), Field, Aero, BatterAttributes.LeagueAverage, fielders, coeff, new Xoshiro256Random(seed + 1));
            if (res == BattedBallResult.Error) errors++;
        }
        return (double)errors / trials;
    }

    [Fact]
    public void GroundBallError_LeagueAverageThrowAccuracy_MatchesCatchOnlyBaseline()
    {
        // ThrowErrorBaseProb=0（既定）なのでThrowAccuracy=50では送球ロールの寄与が0＝捕球ロール単独の
        // 従来挙動（ErrorBaseProb単体）と恒等のはず。
        var coeff = new FieldingCoefficients();
        var catchOnlyRate = GroundBallErrorRate(50, coeff with { ThrowErrorBaseProb = -1.0, ThrowErrorAccuracySlope = 0.0 });
        // ThrowErrorBaseProb=-1.0はMathUtil.Clampで0にクランプされ「送球ロールは絶対に成立しない」を保証する
        // ダミー設定＝捕球ロールのみを踏んだ場合の基準値。
        var rate = GroundBallErrorRate(50, coeff);
        Assert.Equal(catchOnlyRate, rate, 3);
    }

    [Fact]
    public void GroundBallError_LowerThrowAccuracy_IncreasesErrorRateMonotonically()
    {
        var coeff = new FieldingCoefficients();
        var rateWorst = GroundBallErrorRate(0, coeff);
        var rateAverage = GroundBallErrorRate(50, coeff);
        var rateBest = GroundBallErrorRate(100, coeff);

        Assert.True(rateWorst > rateAverage, $"worst={rateWorst} average={rateAverage}");
        Assert.True(rateAverage >= rateBest, $"average={rateAverage} best={rateBest}");
    }

    [Fact]
    public void FlyBallError_IsUnaffectedByThrowAccuracy()
    {
        // 浅いフライ（AtBatPipelineTests.FieldingResolver_CanOfCornFly_IsCaughtOut と同条件）は
        // MaybeError（Catchingのみ）を通るため、ThrowAccuracyを変えても結果が1シードたりとも変わらない。
        var ball = new BattedBall { ExitVelocityMps = 95.0 / 3.6, LaunchAngleDeg = 45, BearingDeg = 0 };
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0.15 }; // 失策が十分な頻度で起きるよう引き上げ
        var lowAccuracy = Field.StandardAlignment(new FielderAttributes { Catching = 50, ThrowAccuracy = 0 });
        var highAccuracy = Field.StandardAlignment(new FielderAttributes { Catching = 50, ThrowAccuracy = 100 });

        for (ulong seed = 0; seed < 200; seed++)
        {
            var a = FieldingResolver.Resolve(ball, Field, Aero, BatterAttributes.LeagueAverage, lowAccuracy, coeff, new Xoshiro256Random(seed));
            var b = FieldingResolver.Resolve(ball, Field, Aero, BatterAttributes.LeagueAverage, highAccuracy, coeff, new Xoshiro256Random(seed));
            Assert.Equal(a, b);
        }
    }
}

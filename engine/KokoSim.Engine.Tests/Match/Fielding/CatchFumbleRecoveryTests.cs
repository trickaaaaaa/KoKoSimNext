using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 捕球ミス（お手玉）の回収レース（design-14 P1-6 追記, issue #817）。送球逸れの回収レース
/// （<see cref="ThrowAccuracyErrorTests"/> の WildThrowRecovery_* と同じ形）を捕球ミスにも適用し、
/// 「弾いたら即失策」ではなく、こぼれ幅が小さければ拾い直してアウトに戻ることを確認する。
/// </summary>
public sealed class CatchFumbleRecoveryTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    // 内野ゴロ（遊撃へ強めの当たり）。一塁送球が確実に間に合ってアウトになる＝捕球ロールが載る打球。
    private static FieldingPlay Grounder(FieldingCoefficients coeff, ulong seed)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 34.0, LaunchAngleDeg = -4.0, BearingDeg = -12.0 },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes()),
            coeff, new Xoshiro256Random(seed));

    private static double ErrorRate(FieldingCoefficients coeff, int trials = 6000)
    {
        var errors = 0;
        for (ulong seed = 1; seed <= (ulong)trials; seed++)
            if (Grounder(coeff, seed).Result == BattedBallResult.Error) errors++;
        return (double)errors / trials;
    }

    [Fact]
    public void FumbleRecovery_TinyScatter_LowerErrorRate_ThanHugeScatter()
    {
        // こぼれ幅が小さいほど処理野手の拾い直しが間に合いやすく、捕球エラーが確定していても
        // 最終的な失策(ReachedOnError)率は下がる（拾い直しでアウトに戻るケースが増える）。
        // ErrorBaseProb=1.0・TunnelShareProb=0・ErrorMaxProb=1.0＝毎回Catchエラーを引かせ、
        // 回収レースの効果だけを測る。
        var tiny = new FieldingCoefficients
            { ErrorBaseProb = 1.0, ErrorMaxProb = 1.0, TunnelShareProb = 0.0, CatchFumbleScatterSigmaM = 0.01 };
        var huge = new FieldingCoefficients
            { ErrorBaseProb = 1.0, ErrorMaxProb = 1.0, TunnelShareProb = 0.0, CatchFumbleScatterSigmaM = 500.0 };
        var tinyRate = ErrorRate(tiny);
        var hugeRate = ErrorRate(huge);
        Assert.True(tinyRate < hugeRate, $"こぼれ幅が小さいのに失策率が下がっていない: tiny={tinyRate:F3} huge={hugeRate:F3}");
    }

    [Fact]
    public void FumbleRecovery_HugeScatter_AlmostAlwaysErrors()
    {
        // こぼれ幅が極端に大きければ拾い直しは絶対に間に合わない＝従来の二値モデル（捕球エラー=即失策）へ縮退する。
        var coeff = new FieldingCoefficients
            { ErrorBaseProb = 1.0, ErrorMaxProb = 1.0, TunnelShareProb = 0.0, CatchFumbleScatterSigmaM = 20000.0 };
        var rate = ErrorRate(coeff, trials: 2000);
        Assert.True(rate > 0.80, $"こぼれ幅が極端に大きいのに失策率が下がりすぎている: {rate:F3}");
    }

    [Fact]
    public void FumbleRecovery_TinyScatter_SomePlaysRecoverToOut_WithDisplayData()
    {
        // こぼれ幅が小さければ一部の打席はアウトに戻り、絵の入力（RecoveredCatchScatterM/OutSeconds）を持つ。
        var coeff = new FieldingCoefficients
            { ErrorBaseProb = 1.0, ErrorMaxProb = 1.0, TunnelShareProb = 0.0, CatchFumbleScatterSigmaM = 0.01 };
        var recoveredAny = false;
        for (ulong seed = 1; seed <= 500; seed++)
        {
            var play = Grounder(coeff, seed);
            if (play.Result == BattedBallResult.Out && play.RecoveredCatchScatterM is { } scatter)
            {
                recoveredAny = true;
                Assert.Null(play.ErrorKind);
                Assert.NotNull(play.RecoveredCatchOutSeconds);
                Assert.True(scatter >= 0.0);
            }
        }
        Assert.True(recoveredAny, "こぼれ幅が小さいのに一度も拾い直してアウトに戻らなかった");
    }
}

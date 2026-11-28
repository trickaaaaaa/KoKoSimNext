using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 一塁送球まわりの失策判定の細部（issue #1260）。
/// 送球していないアンアシスト刺殺（3U）に悪送球・受け手の捕球ミスを引かない／一塁手処理の送球は投手カバーが受け手。
/// </summary>
public sealed class FirstBaseThrowErrorDetailsTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    private static FieldingCoefficients Coeff(double throwErrorBase) => new()
    {
        ErrorBaseProb = 0, ErrorMinProb = 0, ErrorMaxProb = 1.0,
        InfieldHandleJitterMeanSeconds = 0,
        ThrowErrorBaseProb = throwErrorBase, ReceiveErrorBaseProb = 0.9,
    };

    private static Dictionary<FieldPosition, FielderAttributes> Alignment(int pitcherCatching)
    {
        var d = new Dictionary<FieldPosition, FielderAttributes>();
        foreach (FieldPosition pos in System.Enum.GetValues(typeof(FieldPosition)))
            d[pos] = new FielderAttributes { Fielding = 50, Speed = 50, ArmStrength = 50 };
        d[FieldPosition.Pitcher] = new FielderAttributes { Fielding = 50, Speed = 50, ArmStrength = 50, Catching = pitcherCatching };
        return d;
    }

    private static FieldingPlay Grounder(double bearingDeg, int pitcherCatching, ulong seed, double throwErrorBase)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 22.0, LaunchAngleDeg = 3.0, BearingDeg = bearingDeg },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.AlignmentFrom(Alignment(pitcherCatching)),
            Coeff(throwErrorBase), new Xoshiro256Random(seed));

    [Fact]
    public void UnassistedPutout_NeverDrawsThrowOrReceiveError()
    {
        for (ulong seed = 1; seed <= 300; seed++)
        {
            var play = Grounder(bearingDeg: 38.0, pitcherCatching: 50, seed, throwErrorBase: 0.9);
            Assert.True(play.UnassistedPutout, "テスト前提: 一塁線寄りの弱いゴロは3U");
            Assert.Equal(BattedBallResult.Out, play.Result);
            Assert.Null(play.ErrorKind);
        }
    }

    [Fact]
    public void FirstBasemanThrowToPitcher_ReceiverIsPitcher()
    {
        var receive = 0;
        var poorErrors = 0;
        var eliteErrors = 0;
        // 方位30°＝捕球点がベースから約8m（一塁手が投手へトスする側。issue #1685 で分かれ目が外へ寄ったので30°→28°、
        // issue #1789 で内野の到達に加速を入れ、28〜29°は400本に1本ほど二塁手が捕るようになったので30°へ戻した）
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var poor = Grounder(bearingDeg: 30.0, pitcherCatching: 10, seed, throwErrorBase: 0.0);
            Assert.Equal(FieldPosition.FirstBase, poor.FielderRole);
            if (poor.UnassistedPutout) continue;   // 捕球点が seed で揺れる。送球する場合だけ見る
            if (poor.ErrorKind == FieldingErrorKind.Receive)
            {
                receive++;
                Assert.Equal(FieldPosition.Pitcher, poor.ErrorFielderRole);
            }
            if (poor.Result == BattedBallResult.Error) poorErrors++;
            if (Grounder(bearingDeg: 30.0, pitcherCatching: 95, seed, throwErrorBase: 0.0).Result == BattedBallResult.Error) eliteErrors++;
        }
        Assert.True(receive > 0, "一塁手処理の送球に受け手（投手）の捕球ミスが引かれていない");
        Assert.True(poorErrors > eliteErrors, $"受け手＝投手の捕球力が効いていない: 低{poorErrors} 高{eliteErrors}");
    }
}

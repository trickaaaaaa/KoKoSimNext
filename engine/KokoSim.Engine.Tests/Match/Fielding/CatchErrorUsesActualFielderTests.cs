using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 内野ゴロの捕球ミス判定は、捕球点の最寄りの野手でなく実際に捕った野手（処理野手）の Catching で引く（issue #985）。
/// </summary>
public sealed class CatchErrorUsesActualFielderTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    // Catching 0 → 確率1.0（上限へ）／Catching 100 → 下限。他の失策段は止めて、捕球ミスだけを見る。
    private static readonly FieldingCoefficients CatchOnly = new()
    {
        ErrorBaseProb = 0.5,
        ErrorCatchingSlope = 0.01,
        ErrorCatchingSlopeStrong = 0.01,
        ErrorMaxProb = 1.0,
        ThrowErrorBaseProb = 0.0,
        ReceiveErrorBaseProb = 0.0,
        TunnelShareProb = 0.0,
        OutfieldFumbleBaseProb = 0.0,
    };

    private static IReadOnlyList<Fielder> Alignment(FieldPosition? special, int specialCatching, int othersCatching)
    {
        var list = new List<Fielder>();
        foreach (var f in Field.StandardAlignment())
            list.Add(f with
            {
                Attributes = new FielderAttributes
                {
                    Fielding = 50,
                    Catching = f.Position == special ? specialCatching : othersCatching,
                },
            });
        return list;
    }

    private static FieldingPlay Play(BattedBall ball, IReadOnlyList<Fielder> fielders, ulong seed)
        => FieldingResolver.ResolveDetailed(
            ball, Field, Aero, BatterAttributes.LeagueAverage, fielders, CatchOnly, new Xoshiro256Random(seed));

    private static Fielder Nearest(IReadOnlyList<Fielder> fielders, double x, double z)
        => fielders.OrderBy(f => (f.Location.X - x) * (f.Location.X - x) + (f.Location.Z - z) * (f.Location.Z - z)).First();

    /// <summary>処理野手と捕球点の最寄りの野手が別人になる打球を探す（決定論）。</summary>
    private static (BattedBall Ball, FieldPosition Handler, FieldPosition Nearest) FindMismatch()
    {
        var probe = Alignment(null, 50, 50);
        foreach (var ev in new[] { 12.0, 18.0, 25.0, 32.0 })
        foreach (var bearing in Enumerable.Range(-45, 91))
        {
            var ball = new BattedBall { ExitVelocityMps = ev, LaunchAngleDeg = 2.0, BearingDeg = bearing };
            var play = Play(ball, probe, 1);
            if (play.Result != BattedBallResult.Out) continue;
            if (play.FielderRole is not { } handler || play.FieldedX is not { } x || play.FieldedZ is not { } z) continue;
            var nearest = Nearest(probe, x, z).Position;
            if (nearest != handler) return (ball, handler, nearest);
        }
        throw new Xunit.Sdk.XunitException("処理野手と最寄りの野手が違う打球が見つからなかった");
    }

    private static int CatchErrors(BattedBall ball, IReadOnlyList<Fielder> fielders, int trials)
    {
        var n = 0;
        for (ulong seed = 1; seed <= (ulong)trials; seed++)
        {
            var play = Play(ball, fielders, seed);
            if (play.Result == BattedBallResult.Error && play.ErrorKind == FieldingErrorKind.Catch) n++;
        }
        return n;
    }

    [Fact]
    public void CatchError_UsesTheFielderWhoActuallyCaught_NotTheNearestToTheCatchPoint()
    {
        var (ball, handler, nearest) = FindMismatch();
        Assert.NotEqual(handler, nearest);

        // 実捕球者だけ下手（0）・他は名手（100）→ 捕球ロールまで進んだ打席はほぼ全部失策（セーフの打席は
        // ロールが無いので200には届かない）。逆（実捕球者だけ名手）→ ほぼ失策なし。
        var handlerBad = CatchErrors(ball, Alignment(handler, 0, 100), 200);
        var handlerGood = CatchErrors(ball, Alignment(handler, 100, 0), 200);

        Assert.True(handlerBad > 60 && handlerBad > handlerGood * 5, $"実捕球者が下手なのに捕球ミスが少ない: {handlerBad}/200");
        Assert.True(handlerGood < 20, $"実捕球者が名手なのに捕球ミスが多い（最寄り{nearest}の能力で引いている疑い）: {handlerGood}/200");
    }
}

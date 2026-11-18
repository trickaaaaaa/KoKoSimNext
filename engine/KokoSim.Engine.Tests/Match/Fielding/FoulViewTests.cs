using System.Linq;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 守備ビューで見せるファウル（issue #1074・オーナー決定 2026-11-17）。
/// (a) 野手が落下点に着く時刻 − 球の落下時刻 ≤ 0.5秒のファウルフライ／(b) ポール際・スタンドインの大ファウル
/// だけを守備ビューへ移す。判定は純関数で乱数を引かない。
/// </summary>
public sealed class FoulViewTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldingCoefficients Coeff = new();
    private static readonly FieldGeometry Field = new();

    private static FoulChase ChaseLate(double lateSeconds)
        => new(FieldPosition.FirstBase, 20, 20, ReachSeconds: 4.0 + lateSeconds, HangSeconds: 4.0);

    [Fact]
    public void Coefficients_AreTheOwnersDecision()
    {
        Assert.Equal(0.5, Coeff.FoulViewChaseSeconds, 9);
    }

    [Theory]
    [InlineData(0.01, FoulViewKind.ChasedFly)]
    [InlineData(0.30, FoulViewKind.ChasedFly)]
    [InlineData(0.50, FoulViewKind.ChasedFly)]   // ちょうど0.5秒は含む
    [InlineData(0.51, FoulViewKind.None)]
    [InlineData(2.00, FoulViewKind.None)]
    public void ChasedFly_OnlyWithinHalfASecond(double late, FoulViewKind expected)
    {
        Assert.Equal(expected, FoulView.Classify(70, 20, ChaseLate(late), Field, Coeff));
    }

    [Fact]
    public void NoChase_ShortFoul_StaysInDuelView()
    {
        Assert.Equal(FoulViewKind.None, FoulView.Classify(50, 30, chase: null, Field, Coeff));
    }

    [Theory]
    [InlineData(46.0, 100.0, FoulViewKind.BigFoul)]    // ポールのすぐ外・両翼(95m)より遠い
    [InlineData(-52.0, 96.0, FoulViewKind.BigFoul)]    // 三塁側も同じ
    [InlineData(46.0, 90.0, FoulViewKind.None)]        // 両翼に届かない＝ファウルグラウンドに落ちる
    [InlineData(60.0, 120.0, FoulViewKind.None)]       // ラインから離れすぎ（ポール際ではない）
    public void BigFoul_NearThePoleAndBeyondTheFence(double bearing, double along, FoulViewKind expected)
    {
        Assert.Equal(expected, FoulView.Classify(bearing, along, chase: null, Field, Coeff));
    }

    [Fact]
    public void AlongLine_IsTheProjectionOntoTheFoulLine()
    {
        // ちょうどライン上（45°）なら距離そのもの
        var x = 100 * System.Math.Sin(System.Math.PI / 4);
        var z = 100 * System.Math.Cos(System.Math.PI / 4);
        Assert.Equal(100, FoulView.AlongLine(x, z), 6);
        Assert.Equal(100, FoulView.AlongLine(-x, z), 6);
    }

    // ── タイムライン（BuildFoulFlight） ──

    [Fact]
    public void Timeline_ChasedFly_HasTheChaserRunToTheLandingPoint()
    {
        var ball = new BattedBall { ExitVelocityMps = 18.0, LaunchAngleDeg = 75.0, BearingDeg = 70.0 };
        var ground = BallisticIntegrator.IntegrateToGround(ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), Aero);
        Assert.True(Field.InFoulTerritory(ground.LandingPosition), "前提: ファウルグラウンドに落ちる");
        var chase = new FoulChase(FieldPosition.FirstBase, 19.0, 24.0, ground.HangTimeSeconds + 0.3, ground.HangTimeSeconds);

        var t = TimelineBuilder.BuildFoulFlight(ball, Aero, Coeff, Field, false, false, false, chase);

        Assert.Equal(FoulViewKind.ChasedFly, t.FoulView);
        var move = Assert.Single(t.Moves);
        Assert.Equal(FieldPosition.FirstBase, move.Role);
        Assert.Equal(FielderTask.Chase, move.Task);
        Assert.Equal(ground.LandingPosition.X, move.To.X, 6);
        Assert.Equal(ground.LandingPosition.Z, move.To.Z, 6);
        // 着くのは球が落ちた後（届かなかった）＝判定と同じ到達時刻
        var landAt = t.Ball[1].T1;
        Assert.Equal(landAt + 0.3, move.T1, 6);
        Assert.True(t.Duration > move.T1);
        // 出発点は判定が測った実際の守備位置
        Assert.Equal(new TimelinePoint(19.0, 24.0), t.FielderStarts![FieldPosition.FirstBase]);
        // 守備ビューで見せるファウルは「ファール」の判定を1つ積む（issue #1134）。確定は球が落ちた時刻
        var foulJudge = Assert.Single(t.Judgements);
        Assert.Equal(JudgementKind.Foul, foulJudge.Kind);
        Assert.Equal(landAt, foulJudge.T, 6);
        Assert.Equal(-0.3, t.CatchMarginSeconds!.Value, 6);   // 届かなかった＝余裕は負（3Dが「届かない」芝居を選ぶ）
    }

    [Fact]
    public void Timeline_ChaseTooLate_StaysInDuelViewWithoutMoves()
    {
        var ball = new BattedBall { ExitVelocityMps = 18.0, LaunchAngleDeg = 75.0, BearingDeg = 70.0 };
        var chase = new FoulChase(FieldPosition.FirstBase, 19.0, 24.0, ReachSeconds: 9.0, HangSeconds: 4.0);

        var t = TimelineBuilder.BuildFoulFlight(ball, Aero, Coeff, Field, false, false, false, chase);

        Assert.Equal(FoulViewKind.None, t.FoulView);
        Assert.Empty(t.Moves);
        Assert.Empty(t.Judgements);   // 対決ビューのまま流れるファウルは球審コールが出る＝二重にしない
    }

    [Fact]
    public void Timeline_BigFoul_IsShownInTheFieldView()
    {
        var ball = new BattedBall { ExitVelocityMps = 46.0, LaunchAngleDeg = 28.0, BearingDeg = -47.0 };
        var ground = BallisticIntegrator.IntegrateToGround(ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), Aero);
        Assert.True(FoulView.AlongLine(ground.LandingPosition.X, ground.LandingPosition.Z) >= Field.LeftFenceM,
            "前提: 両翼より遠くまで飛ぶ");

        var t = TimelineBuilder.BuildFoulFlight(ball, Aero, Coeff, Field, false, false, false);

        Assert.Equal(FoulViewKind.BigFoul, t.FoulView);
        Assert.Empty(t.Moves);
        // スタンドへ入ったところで終わる（地面の高さの落下点まで追わない）
        Assert.True(t.Duration < t.Ball[1].T1, $"再生が落下点まで続いている（尺 {t.Duration:F2} / 落下 {t.Ball[1].T1:F2}）");
        // 「ファール」はスタンドへ入った時刻に1つ（尺の余韻より前＝見える）
        var foulJudge = Assert.Single(t.Judgements);
        Assert.Equal(JudgementKind.Foul, foulJudge.Kind);
        Assert.True(foulJudge.T < t.Duration && foulJudge.T < t.Ball[1].T1);
    }

    // ── 判定（乱数を引かない・捕れる打球は対象外） ──

    [Fact]
    public void Chase_UsesTheSameReachAsTheVerdict()
    {
        var fielders = Field.StandardAlignment();
        var ball = new BattedBall { ExitVelocityMps = 18.0, LaunchAngleDeg = 75.0, BearingDeg = 70.0 };

        var chase = FoulView.Chase(ball, Field, Aero, fielders, Coeff);

        Assert.NotNull(chase);
        var ground = BallisticIntegrator.IntegrateToGround(ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), Aero);
        var best = fielders.Min(f => (ground.LandingPosition - f.Location).Length / f.Attributes.SprintSpeedMps
                                     + f.Attributes.ReactionDelaySeconds);
        Assert.Equal(best, chase!.ReachSeconds, 9);
        Assert.Equal(ground.HangTimeSeconds, chase.HangSeconds, 9);
    }

    [Fact]
    public void Chase_LowOrIntoTheStands_IsNull()
    {
        var fielders = Field.StandardAlignment();
        // 低い打球（ゴロ）
        Assert.Null(FoulView.Chase(new BattedBall { ExitVelocityMps = 25, LaunchAngleDeg = 2, BearingDeg = 60 },
            Field, Aero, fielders, Coeff));
        // スタンドへ入る打球
        Assert.Null(FoulView.Chase(new BattedBall { ExitVelocityMps = 46, LaunchAngleDeg = 28, BearingDeg = -47 },
            Field, Aero, fielders, Coeff));
    }
}

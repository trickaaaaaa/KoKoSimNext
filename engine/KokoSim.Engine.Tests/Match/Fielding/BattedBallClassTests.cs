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
/// 打球の4分類（ゴロ/バウンド/ライナー/フライ）と、バウンドで内野を抜ける／高く弾んで生きる内野安打が
/// 物理層の積分から創発することの検証（Issue #63 / OPEN-QUESTIONS Q14）。
/// </summary>
public sealed class BattedBallClassTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    private static FieldingPlay Resolve(
        double bearingDeg, double exitMps, double launchDeg,
        FieldingCoefficients coeff, BatterAttributes? batter = null, ulong seed = 12345)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = exitMps, LaunchAngleDeg = launchDeg, BearingDeg = bearingDeg },
            Field, Aero, batter ?? BatterAttributes.LeagueAverage,
            Field.StandardAlignment(), coeff, new Xoshiro256Random(seed));

    /// <summary>強く叩きつけた打球は「バウンド」に分類され、バウンド頂点を持つ（チョッパー）。</summary>
    [Fact]
    public void HardChop_ClassifiesAsBouncer_WithApex()
    {
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, IrregularBounceProb = 0 };
        var play = Resolve(0, exitMps: 45, launchDeg: -14, coeff);

        Assert.Equal(BattedBallClass.Bouncer, play.Class);
        Assert.True(play.BounceApexHeightM >= coeff.BouncerApexThresholdM,
            $"バウンド頂点 {play.BounceApexHeightM:F2}m が閾値 {coeff.BouncerApexThresholdM} 未満");
        Assert.True(play.BounceCount >= 1);
    }

    /// <summary>ゆるいゴロは「ゴロ」に分類され、高くは弾まない。</summary>
    [Fact]
    public void SoftGrounder_ClassifiesAsGrounder()
    {
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, IrregularBounceProb = 0 };
        var play = Resolve(5, exitMps: 26, launchDeg: -4, coeff);

        Assert.Equal(BattedBallClass.Grounder, play.Class);
        Assert.True(play.BounceApexHeightM < coeff.BouncerApexThresholdM);
    }

    /// <summary>低い強い当たりは「ライナー」、高く上がった当たりは「フライ」。</summary>
    [Theory]
    [InlineData(12.0, BattedBallClass.Liner)]
    [InlineData(34.0, BattedBallClass.Fly)]
    public void AirBall_ClassifiedByArc(double launchDeg, BattedBallClass expected)
    {
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, IrregularBounceProb = 0 };
        // 外野へ抜ける当たり（内野に処理されない方位・距離）。
        var play = Resolve(-14, exitMps: 45, launchDeg: launchDeg, coeff);
        Assert.Equal(expected, play.Class);
    }

    /// <summary>
    /// バウンドで内野を抜けて外野へ転がる打球が創発する（頭上を抜ける＝ThroughInfield かつ安打）。
    /// 内野手の届く高さを実戦的な値に保ったまま、強い低いバウンドの探索で少なくとも1つ出ることを確認する。
    /// </summary>
    [Fact]
    public void BounceThroughInfield_Emerges_AsOutfieldHit()
    {
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, IrregularBounceProb = 0 };
        var found = false;
        for (var exit = 42.0; exit <= 55.0 && !found; exit += 1.0)
            for (var launch = -6.0; launch <= 6.0 && !found; launch += 1.0)
                foreach (var bearing in new[] { -24.0, -6.0, 0.0, 6.0, 24.0 })
                {
                    var p = Resolve(bearing, exit, launch, coeff);
                    if (p.ThroughInfield && IsHit(p.Result))
                    {
                        Assert.True(p.Class is BattedBallClass.Grounder or BattedBallClass.Bouncer);
                        Assert.True(p.FieldedZ > coeff.InfieldDepthM - 5.0,
                            $"回収点 {p.FieldedZ:F1}m が内野の外にない");
                        found = true;
                        break;
                    }
                }
        Assert.True(found, "バウンドで内野を抜ける打球が1つも創発しなかった");
    }

    /// <summary>
    /// 高く弾むバウンドで野手が待たされ、間に合わず生きる内野安打が創発する
    /// （内野で処理＝ThrowArriveSeconds あり・Single・Bouncer）。
    /// </summary>
    [Fact]
    public void HighChopper_InfieldSingle_Emerges()
    {
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, IrregularBounceProb = 0 };
        var fast = new BatterAttributes { Speed = 99 };
        var found = false;
        for (var exit = 28.0; exit <= 52.0 && !found; exit += 2.0)
            for (var launch = -22.0; launch <= -8.0 && !found; launch += 1.0)
                foreach (var bearing in new[] { -24.0, -14.0, -6.0, 6.0, 14.0, 24.0 })
                {
                    var p = Resolve(bearing, exit, launch, coeff, batter: fast);
                    if (p is { Result: BattedBallResult.Single, ThroughInfield: false, ThrowArriveSeconds: not null }
                        && p.Class == BattedBallClass.Bouncer)
                    {
                        // 野手が高いバウンドを待つぶん、処理が着地より十分遅れている（待たされている）。
                        Assert.True(p.FieldedAtSeconds > p.HangTimeSeconds + 0.5);
                        found = true;
                        break;
                    }
                }
        Assert.True(found, "高バウンドで生きる内野安打が創発しなかった");
    }

    /// <summary>イレギュラーバウンド（確率1）は必ずフラグが立つ（決定論的に一律確率で発動）。</summary>
    [Fact]
    public void IrregularBounce_AlwaysFlags_WhenProbOne()
    {
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, IrregularBounceProb = 1.0 };
        var play = Resolve(5, exitMps: 30, launchDeg: -4, coeff);
        Assert.True(play.IrregularBounce, "確率1でイレギュラーが立っていない");
    }

    /// <summary>イレギュラーバウンドはエラー率を押し上げる（一律確率＋エラー加算, Issue #63 (c)）。</summary>
    [Fact]
    public void IrregularBounce_RaisesErrorRate()
    {
        var errors = 0;
        var regular = 0;
        var withIrr = new FieldingCoefficients { IrregularBounceProb = 1.0, ErrorIrregularBonus = 0.30 };
        var without = new FieldingCoefficients { IrregularBounceProb = 0.0 };

        for (ulong seed = 1; seed <= 3000; seed++)
        {
            var a = Resolve(6, 28, -3, withIrr, seed: seed);
            var b = Resolve(6, 28, -3, without, seed: seed);
            if (a.Result == BattedBallResult.Error) errors++;
            if (b.Result == BattedBallResult.Error) regular++;
        }

        Assert.True(errors > regular + 50,
            $"イレギュラーでエラーが増えていない（irr={errors} / reg={regular}）");
    }

    private static bool IsHit(BattedBallResult r) =>
        r is BattedBallResult.Single or BattedBallResult.Double or BattedBallResult.Triple;
}

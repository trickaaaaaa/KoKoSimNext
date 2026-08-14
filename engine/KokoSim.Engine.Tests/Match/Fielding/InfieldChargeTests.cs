using System.Collections.Generic;
using System.Globalization;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 前進捕球（チャージ, issue #630）。
///
/// <para>回帰の実害: 処理野手の捕球点が「守備位置に最も近く打球が通る点」固定＝<b>前へ出ない</b>実装だったため、
/// ボテボテのゴロほど野手が守備位置で突っ立って球を待ち、打者走者が余裕で一塁を陥れていた。判定だけでなく
/// 3D守備ビューの絵にも「待ち姿」としてそのまま出ていた（オーナー報告 2026-08-12）。</para>
///
/// <para>ここで固定するのは ①緩い打球で捕球が早く・本塁寄りになる ②強い打球は<b>完全に</b>従来どおり
/// ③頭上抜け判定と処理野手の割り当て（#261/#63 の帯）を巻き込まない ④前進のぶん送球の体勢が悪くなる、の4点。
/// 乱数は引かない（幾何と時間だけ＝不変条件#1/#2）。</para>
/// </summary>
public sealed class InfieldChargeTests
{
    private readonly ITestOutputHelper _out;
    public InfieldChargeTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    /// <summary>失策乱数を止めて捕球点・時刻だけを見る（InfieldGrounderTimingTests と同型）。</summary>
    private static readonly FieldingCoefficients On = new() { ErrorBaseProb = 0, ThrowErrorBaseProb = 0 };

    /// <summary>前進量の上限0＝チャージ無効（修正前の挙動）。</summary>
    private static readonly FieldingCoefficients Off = On with
    {
        ChargeMaxAdvanceCornerM = 0.0,
        ChargeMaxAdvanceMiddleM = 0.0,
    };

    private static FieldingPlay Grounder(
        double exitVelocityMps, double bearingDeg, FieldingCoefficients coeff, double launchAngleDeg = 4.0)
        => FieldingResolver.ResolveDetailed(
            new BattedBall
            {
                ExitVelocityMps = exitVelocityMps, LaunchAngleDeg = launchAngleDeg, BearingDeg = bearingDeg,
            },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = 50 }),
            coeff, new Xoshiro256Random(777));

    private static double DistanceFromHome(FieldingPlay p)
    {
        var x = p.FieldedX ?? 0.0;
        var z = p.FieldedZ ?? 0.0;
        return System.Math.Sqrt(x * x + z * z);
    }

    // ===== ① 緩いゴロは前へ出て捕る =====

    [Theory]
    [InlineData(12.0, -18.0)]   // 三塁線寄りのボテボテ
    [InlineData(12.0, -6.0)]    // 投手前のボテボテ
    [InlineData(12.0, 8.0)]     // 一二塁間のボテボテ
    [InlineData(16.0, 20.0)]    // 一塁線寄りの緩いゴロ
    public void SlowGrounder_IsChargedForward(double exit, double bearing)
    {
        var off = Grounder(exit, bearing, Off);
        var on = Grounder(exit, bearing, On);

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0} 捕球 {1:F3}s→{2:F3}s / 本塁から {3:F1}m→{4:F1}m / 送球到達 {5:F3}s→{6:F3}s / {7}→{8}",
            on.FielderRole, off.FieldedAtSeconds, on.FieldedAtSeconds,
            DistanceFromHome(off), DistanceFromHome(on),
            off.ThrowArriveSeconds, on.ThrowArriveSeconds, off.Result, on.Result));

        // 処理野手は変わらない（#261 の割り当てには手を入れない）。
        Assert.Equal(off.FielderRole, on.FielderRole);
        Assert.True(on.FieldedAtSeconds < off.FieldedAtSeconds - 0.05,
            $"緩いゴロなのに捕球が早くならない（{off.FieldedAtSeconds:F3}→{on.FieldedAtSeconds:F3}s）＝待ち姿のまま");
        Assert.True(DistanceFromHome(on) < DistanceFromHome(off) - 0.5,
            $"捕球点が本塁寄りへ動かない（{DistanceFromHome(off):F1}→{DistanceFromHome(on):F1}m）");
        // 前進のぶん体勢が悪い（握り替え＋精度）ぶんを差し引いても、送球到達は早くなる。
        Assert.True(on.ThrowArriveSeconds < off.ThrowArriveSeconds,
            "前へ出たのに一塁への到達が早くならない（ペナルティが利得を食い潰している）");
    }

    /// <summary>ボテボテを走者が走り抜けるだけの内野安打が、突っ込めば刺せるようになる。</summary>
    [Fact]
    public void SlowRoller_ToThePitcher_TurnsFromInfieldHitIntoAnOut()
    {
        var off = Grounder(12.0, -6.0, Off);
        var on = Grounder(12.0, -6.0, On);

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "投手前のボテボテ: 捕球 {0:F3}→{1:F3}s / 走者 {2:F3}s / margin {3:F3}→{4:F3}s / {5}→{6}",
            off.FieldedAtSeconds, on.FieldedAtSeconds, on.BatterToFirstSeconds,
            off.JudgementMarginSeconds, on.JudgementMarginSeconds, off.Result, on.Result));

        Assert.Equal(BattedBallResult.Single, off.Result);
        Assert.Equal(BattedBallResult.Out, on.Result);
    }

    // ===== ② しきい以上の打球は従来と完全に同じ／強いほど前進しない =====

    /// <summary>
    /// 打球速度がしきい以上なら前進量は0＝<b>捕球点も処理時刻も送球もビット単位で修正前と同じ</b>。
    /// しきいを 1m/s へ落として「事実上すべての打球がしきい以上」の状態を作り、恒等であることを固定する
    /// （＝チャージは<b>しきいより遅い打球にしか触らない</b>ことの証明。帯を動かす犯人捜しのとき、
    /// まずここを見れば「チャージ経路は無関係」と切れる）。
    /// </summary>
    [Fact]
    public void AboveTheSlownessThreshold_NothingChangesAtAll()
    {
        var above = On with { ChargeSlowBallSpeedMps = 1.0 };
        foreach (var exit in new[] { 16.0, 24.0, 32.0, 40.0 })
        {
            foreach (var bearing in new[] { -30.0, -18.0, -6.0, 8.0, 20.0 })
            {
                var off = Grounder(exit, bearing, Off);
                var on = Grounder(exit, bearing, above);
                Assert.Equal(off.FielderRole, on.FielderRole);
                Assert.Equal(off.FieldedAtSeconds, on.FieldedAtSeconds);
                Assert.Equal(off.FieldedX, on.FieldedX);
                Assert.Equal(off.FieldedZ, on.FieldedZ);
                Assert.Equal(off.ThrowArriveSeconds, on.ThrowArriveSeconds);
                Assert.Equal(off.FielderThrowAccuracy, on.FielderThrowAccuracy);
                Assert.Equal(off.Result, on.Result);
            }
        }
    }

    /// <summary>
    /// 強い打球ほど前へ出ない（前進量は打球速度に対して単調に0へ）＝しきいの前後で絵と判定が飛ばない。
    /// 処理野手が入れ替わると幾何ごと変わるので、<b>同じ野手が処理する帯</b>（三遊間の遊撃）で見る。
    /// </summary>
    [Fact]
    public void ChargeDistance_ShrinksAsTheBallGetsHarder()
    {
        var advances = new List<double>();
        foreach (var exit in new[] { 24.0, 28.0, 32.0, 36.0 })
        {
            var off = Grounder(exit, -18.0, Off);
            var on = Grounder(exit, -18.0, On);
            var advance = DistanceFromHome(off) - DistanceFromHome(on);
            advances.Add(advance);
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "初速{0,4:F0}m/s {1,-11} 前進 {2:F2}m（{3:F1}→{4:F1}m）",
                exit, on.FielderRole, advance, DistanceFromHome(off), DistanceFromHome(on)));
            Assert.Equal(FieldPosition.Shortstop, on.FielderRole);
        }
        for (var i = 1; i < advances.Count; i++)
        {
            Assert.True(advances[i] < advances[i - 1],
                $"打球が強くなったのに前進量が減らない（{advances[i - 1]:F2}m → {advances[i]:F2}m）");
            Assert.True(advances[i] >= 0.0, "後ろへ下がっている（前進捕球が逆向き）");
        }
    }

    // ===== ③ 頭上抜け・処理野手の割り当てを巻き込まない（#261/#63 の帯） =====

    [Fact]
    public void ThroughInfield_AndFielderAssignment_AreUntouched()
    {
        var checkedPlays = 0;
        foreach (var exit in new[] { 12.0, 16.0, 20.0, 24.0, 28.0, 32.0, 36.0, 40.0, 44.0 })
        {
            foreach (var bearing in new[] { -40.0, -30.0, -20.0, -10.0, 0.0, 10.0, 20.0, 30.0, 40.0 })
            {
                foreach (var launch in new[] { -2.0, 4.0, 10.0 })
                {
                    var off = Grounder(exit, bearing, Off, launch);
                    var on = Grounder(exit, bearing, On, launch);
                    checkedPlays++;
                    Assert.Equal(off.ThroughInfield, on.ThroughInfield);
                    Assert.Equal(off.FielderRole, on.FielderRole);
                    // 内野を抜けた打球は外野の集約モデルへ渡る＝チャージは一切効かない。
                    if (!off.ThroughInfield) continue;
                    Assert.Equal(off.FieldedAtSeconds, on.FieldedAtSeconds);
                    Assert.Equal(off.Result, on.Result);
                }
            }
        }
        _out.WriteLine($"照合 {checkedPlays} 打球: 頭上抜け・処理野手ともに一致");
        Assert.True(checkedPlays > 200);
    }

    // ===== ④ 役割差と送球ペナルティ =====

    /// <summary>前進量の上限は役割ごとの係数から来る（コーナー＝投手・一塁手・三塁手／ミドル＝二遊間）。</summary>
    [Fact]
    public void ChargeDepth_FollowsTheRoleCoefficient()
    {
        // ミドル（二塁手）が処理する緩いゴロ: ミドル側の上限だけが効く。
        var middleShallow = Grounder(20.0, 8.0, On with { ChargeMaxAdvanceMiddleM = 0.5 });
        var middleDeep = Grounder(20.0, 8.0, On with { ChargeMaxAdvanceMiddleM = 8.0 });
        Assert.Equal(FieldPosition.SecondBase, middleDeep.FielderRole);
        Assert.True(DistanceFromHome(middleDeep) < DistanceFromHome(middleShallow) - 0.5,
            $"ミドルの上限を広げても深く出ない（{DistanceFromHome(middleShallow):F1}→{DistanceFromHome(middleDeep):F1}m）");

        // コーナー（投手）が処理する緩いゴロ: ミドル側の上限をどう振っても一切動かない。
        var cornerShallow = Grounder(12.0, -6.0, On with { ChargeMaxAdvanceCornerM = 0.5 });
        var cornerDeep = Grounder(12.0, -6.0, On);
        var cornerOther = Grounder(12.0, -6.0, On with { ChargeMaxAdvanceMiddleM = 0.0 });
        Assert.Equal(FieldPosition.Pitcher, cornerDeep.FielderRole);
        Assert.True(DistanceFromHome(cornerDeep) < DistanceFromHome(cornerShallow) - 0.5,
            $"コーナーの上限を広げても深く出ない（{DistanceFromHome(cornerShallow):F1}→{DistanceFromHome(cornerDeep):F1}m）");
        Assert.Equal(DistanceFromHome(cornerDeep), DistanceFromHome(cornerOther));

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "ミドル {0:F1}→{1:F1}m / コーナー {2:F1}→{3:F1}m",
            DistanceFromHome(middleShallow), DistanceFromHome(middleDeep),
            DistanceFromHome(cornerShallow), DistanceFromHome(cornerDeep)));
    }

    /// <summary>前進捕球は体勢が悪い: 握り替えが伸び、送球精度が落ちる（一方的な守備強化にしない）。</summary>
    [Fact]
    public void Charging_CostsTransferTimeAndThrowAccuracy()
    {
        const double Exit = 12.0, Bearing = -6.0;
        var free = Grounder(Exit, Bearing, On with { ChargeThrowPenaltySeconds = 0.0, ChargeThrowAccuracyPenalty = 0.0 });
        var costly = Grounder(Exit, Bearing, On);

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "ペナルティ無し 到達 {0:F3}s Ac{1} / 有り 到達 {2:F3}s Ac{3}",
            free.ThrowArriveSeconds, free.FielderThrowAccuracy,
            costly.ThrowArriveSeconds, costly.FielderThrowAccuracy));

        Assert.Equal(free.FieldedAtSeconds, costly.FieldedAtSeconds);   // 捕球そのものは同じ
        Assert.True(costly.ThrowArriveSeconds > free.ThrowArriveSeconds, "前進しても握り替えが伸びていない");
        Assert.True(costly.FielderThrowAccuracy < free.FielderThrowAccuracy, "前進しても送球精度が落ちていない");
        // 素の精度（50）からの低下は上限（ChargeThrowAccuracyPenalty）を超えない。
        Assert.True(costly.FielderThrowAccuracy >= free.FielderThrowAccuracy - (int)On.ChargeThrowAccuracyPenalty);
    }

    /// <summary>チャージは乱数を引かない＝同じ打球を何度解いても同じ結果（不変条件#2）。</summary>
    [Fact]
    public void Charge_IsDeterministic_AndConsumesNoRandom()
    {
        for (var i = 0; i < 5; i++)
        {
            var a = Grounder(12.0, -6.0, On);
            var b = Grounder(12.0, -6.0, On);
            Assert.Equal(a.FieldedAtSeconds, b.FieldedAtSeconds);
            Assert.Equal(a.FieldedX, b.FieldedX);
            Assert.Equal(a.FieldedZ, b.FieldedZ);
        }
    }
}

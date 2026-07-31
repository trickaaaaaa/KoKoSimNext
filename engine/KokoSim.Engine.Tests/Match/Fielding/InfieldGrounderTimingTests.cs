using System.Globalization;
using KokoSim.Balance;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 内野ゴロの秒軸（issue #358）。捕球→送球の所要が現実の値であること、その上で内野安打が
/// 出過ぎも出なさすぎもしないことを固定する。
/// 回帰の実害: 捕球点の固定オーバーヘッド0.88＋握り替え0.70＝<b>1.58秒</b>（実際の約2倍）が乗っており、
/// 定位置の遊撃ゴロが判定境界のすぐ上＝少し緩い/深いだけで内野安打になっていた（内野ゴロの19.3%）。
/// 2D俯瞰では「捕球点で1.58秒ボールを持ったまま静止する絵」として同じ原因が見えていた。
/// C#既定係数＝Unity実ゲームと同じ経路で計測する（data/coefficients.yaml とは同値に保つ）。
/// </summary>
public sealed class InfieldGrounderTimingTests
{
    private readonly ITestOutputHelper _out;
    public InfieldGrounderTimingTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();
    /// <summary>失策乱数を止め、判定を送球到達時刻だけに絞る（TransferFactorTests と同型）。</summary>
    private static readonly FieldingCoefficients Coeff = new() { ErrorBaseProb = 0, ThrowErrorBaseProb = 0 };

    private static FieldingPlay Grounder(double exitVelocityMps, double bearingDeg, BatterAttributes batter,
        int teamFielding = 50, double launchAngleDeg = 4.0)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = exitVelocityMps, LaunchAngleDeg = launchAngleDeg, BearingDeg = bearingDeg },
            Field, Aero, batter,
            Field.StandardAlignment(new FielderAttributes { Fielding = teamFielding }),
            Coeff, new Xoshiro256Random(777));

    private static BatterAttributes Batter(int speed = 50, Handedness bats = Handedness.Right)
        => BatterAttributes.LeagueAverage with { Speed = speed, Bats = bats };

    // ===== 物理量: 捕ってから離すまで =====

    [Fact]
    public void CatchToRelease_IsWithinRealisticRange()
    {
        // 実際の内野手は捕ってから離すまで0.6〜0.9秒。守備の巧拙で伸縮するのは握り替え側だけ。
        double CatchToRelease(int fielding) => Coeff.InfieldPlayOverheadSeconds
            + Coeff.ThrowTransferSeconds
            * new FielderAttributes { Fielding = fielding }
                .TransferFactor(Coeff.ThrowTransferFieldingSlope, Coeff.ThrowTransferFactorMin);

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "捕球→リリース[s]: 守備20 {0:F3} / 守備50 {1:F3} / 守備90 {2:F3}",
            CatchToRelease(20), CatchToRelease(50), CatchToRelease(90)));

        foreach (var fielding in new[] { 20, 50, 90 })
        {
            var t = CatchToRelease(fielding);
            Assert.True(t is >= 0.60 and <= 0.90,
                $"守備{fielding} の捕球→リリース {t:F3}s が現実域 0.60〜0.90s の外（#358 の回帰: 1.58s）");
        }
        // 巧い野手ほど速い（Issue #36 の単調性を秒軸でも固定）。
        Assert.True(CatchToRelease(90) < CatchToRelease(50) && CatchToRelease(50) < CatchToRelease(20));
    }

    // ===== 定位置のゴロは通常アウト =====

    [Fact]
    public void RoutineShortstopGrounder_IsOut()
    {
        // 三遊間へのふつうのゴロ（初速32m/s＝115km/h）。遊撃が定位置(z≈39m・送球37.6m)で捕る＝処理時刻1.51s。
        // 修正前は「アウトの条件が fieldTime ≤ 1.458s」だったので、この当たりはちょうど内野安打になっていた
        // （守備所要 1.51+1.58+1.17 = 4.26s > 走者 4.23s）。#358 の症状そのもの。
        var play = Grounder(32.0, -18.0, Batter());

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0} 捕球 {1:F3}s → 送球到達 {2:F3}s / 走者 {3:F3}s / margin {4:F3}s → {5}",
            play.FielderRole, play.FieldedAtSeconds, play.ThrowArriveSeconds,
            play.BatterToFirstSeconds, play.JudgementMarginSeconds, play.Result));

        Assert.Equal(FieldPosition.Shortstop, play.FielderRole);
        Assert.InRange(play.FieldedAtSeconds!.Value, 1.4, 1.7);
        Assert.Equal(BattedBallResult.Out, play.Result);
    }

    [Fact]
    public void RoutineGrounders_AcrossTheInfield_AreOuts()
    {
        // 方位を振っても定位置のゴロは通常アウト（1コースだけの偶然でないことを固定）。
        foreach (var bearing in new[] { -30.0, -18.0, -6.0, 8.0, 20.0 })
        {
            var play = Grounder(32.0, bearing, Batter());
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "方位{0,6:F0}° {1,-11} margin {2,6:F3}s → {3}",
                bearing, play.FielderRole, play.JudgementMarginSeconds, play.Result));
            Assert.Equal(BattedBallResult.Out, play.Result);
        }
    }

    // ===== 快足・左打者・深い当たりでは内野安打が出る =====

    [Fact]
    public void SlowRoller_ToDeepHole_IsInfieldHit()
    {
        // 三遊間の深い所（遊撃の定位置）へ転がる緩いゴロ（初速24m/s＝86km/h）。捕球が2.18秒まで遅れて間に合わない。
        var play = Grounder(24.0, -18.0, Batter());

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0} 捕球 {1:F3}s → 送球到達 {2:F3}s / 走者 {3:F3}s / margin {4:F3}s → {5}",
            play.FielderRole, play.FieldedAtSeconds, play.ThrowArriveSeconds,
            play.BatterToFirstSeconds, play.JudgementMarginSeconds, play.Result));

        Assert.Equal(BattedBallResult.Single, play.Result);
    }

    [Fact]
    public void FastRunner_AndLeftBatter_BeatTheThrow_OnTheSamePlay()
    {
        // 同じ打球でも、走力・左右で間に合う/間に合わないが入れ替わること（走者側の秒軸が効いている）。
        // 初速28m/s＝101km/hのやや緩い遊ゴロ＝平均走者ならアウト、快足なら内野安打の際どい当たり。
        const double Exit = 28.0, Bearing = -6.0;
        var normal = Grounder(Exit, Bearing, Batter());
        var fast = Grounder(Exit, Bearing, Batter(speed: 92));
        var left = Grounder(Exit, Bearing, Batter(bats: Handedness.Left));

        foreach (var (label, p) in new[] { ("走力50右", normal), ("走力92右", fast), ("走力50左", left) })
        {
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}: 走者 {1:F3}s / margin {2:F3}s → {3}", label, p.BatterToFirstSeconds,
                p.JudgementMarginSeconds, p.Result));
        }

        // 走者が速いほど一塁到達は速く、判定はセーフ寄りへ動く。
        Assert.True(fast.BatterToFirstSeconds < normal.BatterToFirstSeconds);
        Assert.True(left.BatterToFirstSeconds < normal.BatterToFirstSeconds);
        Assert.True(fast.JudgementMarginSeconds > normal.JudgementMarginSeconds);
        Assert.True(left.JudgementMarginSeconds > normal.JudgementMarginSeconds);
        // 平均走者がアウトになる際どい当たりで、快足だけは内野安打になる。
        Assert.Equal(BattedBallResult.Out, normal.Result);
        Assert.Equal(BattedBallResult.Single, fast.Result);
    }

    // ===== 絵: 捕球点でボールを持つ「間」 =====

    [Fact]
    public void Timeline_HoldAtCatchPoint_MatchesCatchToRelease()
    {
        // 2D俯瞰の送球は到達時刻を固定して投げ始めを実距離÷肩から逆算する（Issue #334）ので、
        // 捕球オーバーヘッド＋握り替えがそのまま「捕球点でボールを持ったまま静止する間」として絵に出る。
        // #358 以前はこれが1.58秒だった。判定と絵は同じ係数から出るので、ここで秒を固定しておけば
        // 「とってから投げるのが遅い」の回帰は連番録画を撮る前に捕まえられる。
        var play = Grounder(32.0, -18.0, Batter());
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play));

        var throwSeg = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        const double ContactAt = 0.85;   // TimelineBuilder の接触時刻（private const と同値・既存テストと同じ書き方）
        var fieldedAt = ContactAt + play.FieldedAtSeconds!.Value;
        var hold = throwSeg.T0 - fieldedAt;

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "捕球 {0:F3}s → 投げ始め {1:F3}s（保持 {2:F3}s）→ 到達 {3:F3}s",
            fieldedAt, throwSeg.T0, hold, throwSeg.T1));

        Assert.True(hold is >= 0.60 and <= 0.90,
            $"捕球点でボールを持つ間 {hold:F3}s が現実域 0.60〜0.90s の外（#358 の回帰: 1.58s）");
    }

    // ===== 母集団: 内野安打が出過ぎない・出なさすぎない =====

    [Fact]
    public void InfieldHitShare_IsRealistic_AcrossThePopulation()
    {
        var t = AtBatSimulation.RunInfieldTiming(atBats: 20000, seed: 42);
        _out.WriteLine(t.ToMarkdown());

        Assert.True(t.Grounders > 2000, $"内野ゴロの母数が少なすぎる（{t.Grounders}）");
        // 実際の内野安打はゴロの6〜7%規模。当ゲームは打率・BABIP帯に合わせて11%前後（#358 実測）。
        // 上限は「守備の所要時間を水増しして得点環境を作る」回帰（修正前は19.3%）を、
        // 下限は「オーバーヘッドを削っただけで判定余裕を戻し忘れる」回帰（実測1.5%）を捕まえるためのもの。
        Assert.InRange(t.InfieldHitShare, 0.05, 0.15);
        // 捕ってから離すまでが現実域なら、内野ゴロの大半は余裕を持ったアウトになる。
        Assert.True(t.MarginQuantile(0.50) <= -0.4,
            $"判定margin の中央値 {t.MarginQuantile(0.50):F2}s が境界に寄りすぎ（定位置のゴロが際どくなっている）");
    }
}

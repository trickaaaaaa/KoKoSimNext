using System.Collections.Generic;
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
        int teamFielding = 50, double launchAngleDeg = 4.0, ulong seed = 777)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = exitVelocityMps, LaunchAngleDeg = launchAngleDeg, BearingDeg = bearingDeg },
            Field, Aero, batter,
            Field.StandardAlignment(new FielderAttributes { Fielding = teamFielding }),
            Coeff, new Xoshiro256Random(seed));

    /// <summary>
    /// 同じ打球を種を振って解き、内野安打になった割合を返す。
    ///
    /// <para>2026-08-14 以降、内野の処理時間は<b>もたつき</b>（指数分布）を含む＝同じ当たりでも
    /// 捕るか抜けるかが確率的に決まる。1本の当たりを1つの種で解いて結果を固定するテストは、
    /// 「たまたまその種で引いたもたつき」を仕様として焼いてしまうので率で見る。</para>
    /// </summary>
    private static double InfieldHitRate(double exitVelocityMps, double bearingDeg, BatterAttributes batter,
        int seeds = 200)
    {
        var hits = 0;
        for (ulong s = 1; s <= (ulong)seeds; s++)
        {
            // 送球が間に合わなかった率＝内野安打＋お手玉の失策（2026-11-03）。お手玉の失策は「もたつかなければ
            // アウト」の打球が記録上失策へ振り替わったもので、秒軸（打球の緩さ）の効きはどちらにも同じく出る。
            var p = Grounder(exitVelocityMps, bearingDeg, batter, seed: s * 7919);
            if (p.Result == BattedBallResult.Single || p.ErrorKind == FieldingErrorKind.Bobble) hits++;
        }
        return (double)hits / seeds;
    }

    private static BatterAttributes Batter(int speed = 50, Handedness bats = Handedness.Right)
        => BatterAttributes.LeagueAverage with { Speed = speed, Bats = bats };

    // ===== 物理量: 捕ってから離すまで =====

    [Fact]
    public void CatchToRelease_IsWithinRealisticRange()
    {
        // 実際の内野手は捕ってから離すまで0.6〜0.9秒。ここで測るのは決定論部分（構え直し＋握り替え）だけで、
        // もたつき（指数分布）は InfieldHandSpeedTests が測る。表示の守備から実ゲームと同じ効きを通す（issue #765）。
        var effect = new AbilityEffectCoefficients();
        double CatchToRelease(int fielding) => Coeff.InfieldPlayOverheadSeconds
            + Coeff.ThrowTransferSeconds
            * new Player { Name = "x", Position = FieldPosition.Shortstop, Fielding = fielding }.ToFielder(effect)
                .TransferFactor(Coeff.InfieldTransferFieldingSlope, Coeff.InfieldTransferFactorMin);

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "捕球→リリース[s]: 守備20 {0:F3} / 守備50 {1:F3} / 守備90 {2:F3}",
            CatchToRelease(20), CatchToRelease(50), CatchToRelease(90)));

        foreach (var fielding in new[] { 20, 50, 90 })
        {
            var t = CatchToRelease(fielding);
            Assert.True(t is >= 0.55 and <= 1.00,
                $"守備{fielding} の捕球→リリース {t:F3}s が現実域 0.55〜1.00s の外（#358 の回帰: 1.58s）");
        }
        // 巧い野手ほど速い（Issue #36 の単調性を秒軸でも固定）。
        Assert.True(CatchToRelease(90) < CatchToRelease(50) && CatchToRelease(50) < CatchToRelease(20));
    }

    // ===== 定位置のゴロは通常アウト =====

    [Fact]
    public void RoutineShortstopGrounder_IsOut()
    {
        // 三遊間へのふつうのゴロ（初速32m/s＝115km/h）。遊撃が定位置(z≈39m)まで下がって待つと処理時刻1.51s。
        // 修正前は「アウトの条件が fieldTime ≤ 1.458s」だったので、この当たりはちょうど内野安打になっていた
        // （守備所要 1.51+1.58+1.17 = 4.26s > 走者 4.23s）。#358 の症状そのもの。
        // issue #630 の前進捕球で 3m 詰めて 1.39s（z≈36m）＝強い当たりでも一歩は前へ出る。
        var play = Grounder(32.0, -18.0, Batter());

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0} 捕球 {1:F3}s → 送球到達 {2:F3}s / 走者 {3:F3}s / margin {4:F3}s → {5}",
            play.FielderRole, play.FieldedAtSeconds, play.ThrowArriveSeconds,
            play.BatterToFirstSeconds, play.JudgementMarginSeconds, play.Result));

        Assert.Equal(FieldPosition.Shortstop, play.FielderRole);
        // ミドルの前進上限 4→10m（issue middle-infielder-charge-cap）でさらに一歩前へ＝1.29s。
        Assert.InRange(play.FieldedAtSeconds!.Value, 1.2, 1.7);
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
    public void SlowRoller_ToDeepHole_IsInfieldHit_FarMoreOftenThanARoutineGrounder()
    {
        // 遊撃の深い所へ転がる緩いゴロ（初速19.3m/s≈70km/h）。
        // issue #1685（2026-11-26）で InfieldHandleJitterMeanSeconds 0.49→0.10・
        // BobbleErrorMinJitterSeconds 1.45→0.28 へ縮めたことで、もたつきが作る「際どさの窓」が
        // 1.47秒幅から0.30秒幅へ一桁狭まった。旧シナリオ（24.0m/s, -18.0°）はこの狭い窓の外へ
        // 完全に出てしまい（アウトmarginが0.6秒超）、もたつきの大小に関わらず常にアウトになる
        // ＝率が測れなくなったため選び直した。
        var slow = InfieldHitRate(19.3, -12.0, Batter());
        var routine = InfieldHitRate(27.3, -12.0, Batter());

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "緩いゴロ(19.3m/s) 内野安打 {0:P1} / 平凡なゴロ(27.3m/s) 内野安打 {1:P1}", slow, routine));

        // 実測（issue #1685, 2026-11-26）: 緩いゴロ 36.4% / 同じコースの強いゴロ 0.0%。
        Assert.InRange(slow, 0.15, 0.60);
        Assert.True(routine < 0.30, $"強いゴロが {routine:P1} も抜けている");
        Assert.True(slow > routine + 0.08, "緩いゴロと強いゴロで内野安打率が変わっていない（秒軸が効いていない）");
    }

    [Fact]
    public void FastRunner_AndLeftBatter_BeatTheThrow_OnTheSamePlay()
    {
        // 同じ打球でも、走力・左右で間に合う/間に合わないが入れ替わること（走者側の秒軸が効いている）。
        // issue #1685（2026-11-26）でもたつきの際どさの窓が一桁狭まった分、旧シナリオ（28.0m/s, -6.0°）は
        // 窓の外に出てアウト固定になったため選び直した（初速20.0m/s≈72km/hの遊ゴロ、やや深め）。
        const double Exit = 20.0, Bearing = -12.0;
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
        // 平均走者より快足のほうが内野安打になりやすい（結果は**率**で見る＝もたつきが確率的なため）。
        var normalRate = InfieldHitRate(Exit, Bearing, Batter());
        var fastRate = InfieldHitRate(Exit, Bearing, Batter(speed: 92));
        var leftRate = InfieldHitRate(Exit, Bearing, Batter(bats: Handedness.Left));
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "内野安打率: 走力50右 {0:P1} / 走力92右 {1:P1} / 走力50左 {2:P1}", normalRate, fastRate, leftRate));
        Assert.True(fastRate > normalRate + 0.05, "快足でも内野安打率がほとんど上がらない");
        Assert.True(leftRate > normalRate, "左打者の一塁到達短縮が内野安打率に出ていない");
    }

    // ===== 絵: 捕球点でボールを持つ「間」 =====

    [Fact]
    public void Timeline_HoldAtCatchPoint_MatchesCatchToRelease()
    {
        // 2D俯瞰の送球は到達時刻を固定して投げ始めを実距離÷肩から逆算する（Issue #334）ので、
        // 捕球オーバーヘッド＋握り替えがそのまま「捕球点でボールを持ったまま静止する間」として絵に出る。
        // #358 以前はこれが1.58秒だった。判定と絵は同じ係数から出るので、ここで秒を固定しておけば
        // 「とってから投げるのが遅い」の回帰は連番録画を撮る前に捕まえられる。
        // 2026-08-14 以降、この「間」は**もたつき**（判定境界の校正項を物理層へ移した項）を含む。
        // ＝下限が澱みない処理の 0.80s、上が引いたもたつきぶんだけ伸びる。1本の値では固定できないので
        // 種を振って下限と中央値で押さえる（絵と判定が同じ秒から出ている、が保たれていればよい）。
        var holds = new List<double>();
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var p = Grounder(32.0, -18.0, Batter(), seed: seed * 7919);
            var t = TimelineBuilder.BuildBattedBall(
                p, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(p));
            var seg = Assert.Single(t.Ball, b => b.Kind == BallSegmentKind.Throw);
            const double ContactAt = 0.85;   // TimelineBuilder の接触時刻（private const と同値・既存テストと同じ書き方）
            holds.Add(seg.T0 - (ContactAt + p.FieldedAtSeconds!.Value));
        }
        holds.Sort();
        var min = holds[0];
        var median = holds[holds.Count / 2];

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "捕球点でボールを持つ間[s]: 最小 {0:F3} / 中央 {1:F3} / 最大 {2:F3}", min, median, holds[^1]));

        // 澱みない処理＝オーバーヘッド0.10＋握り替え0.70。ここを下回ったら物理層が現実離れしている
        Assert.True(min >= 0.79 && min <= 0.85, $"もたつき無しの保持 {min:F3}s が 0.80s から外れている");
        // 中央値の上限は #358 の回帰（1.58s）を捕まえるための歯止め
        Assert.True(median <= 1.30, $"保持の中央値 {median:F3}s が長すぎる（#358 の回帰: 1.58s）");
    }

    // ===== 母集団: 内野安打が出過ぎない・出なさすぎない =====

    [Fact]
    public void InfieldHitShare_IsRealistic_AcrossThePopulation()
    {
        var t = AtBatSimulation.RunInfieldTiming(atBats: 20000, seed: 42);
        _out.WriteLine(t.ToMarkdown());

        Assert.True(t.Grounders > 2000, $"内野ゴロの母数が少なすぎる（{t.Grounders}）");
        // issue #1685（2026-11-26）: 旧モデルの 9〜11% は「捕ってから長く球を持つ」もたつき
        // （平均0.49秒・上限1.47秒）が内野安打を水増ししていた分を含む（監督報告）。もたつきを
        // 現実域（平均0.10秒・上限0.30秒）へ縮めた結果、水増しが抜けて 1〜2% へ下がった
        // （実測60,000打席×seed7/42/2024で1.2〜1.4%）。balance-targets.yaml にこの指標の帯は無く
        // （内野安打率そのものは帯で縛っていない）、実際に効くAVG/errors_per_game等は帯内（Heavyで確認済み）。
        // 上限は「守備の所要時間を水増しして得点環境を作る」回帰（#358 の症状・修正前は19.3%）を、
        // 下限は「もたつきの仕組み自体が壊れて内野ゴロが常にアウトになる」回帰（実測0%）を捕まえるためのもの。
        Assert.InRange(t.InfieldHitShare, 0.005, 0.035);
        // 捕ってから離すまでが現実域なら、内野ゴロの大半は余裕を持ったアウトになる。
        Assert.True(t.MarginQuantile(0.50) <= -0.4,
            $"判定margin の中央値 {t.MarginQuantile(0.50):F2}s が境界に寄りすぎ（定位置のゴロが際どくなっている）");
    }
}

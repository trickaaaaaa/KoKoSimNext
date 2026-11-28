using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 内野の到達の式に加速・横移動の割引・強打球の一歩目の遅れを足した効果の回帰（issue #1789 やること4②）。
///
/// <para>採用した既定（2026-11-29・オーナー承認 案R）: 加速（<see cref="FieldingCoefficients.InfielderAccelMps2"/>＝14）と
/// 強打球の一歩目の遅れ（<see cref="FieldingCoefficients.FastGrounderReactionThresholdMps"/> 系）が有効、横移動の割引
/// （<see cref="FieldingCoefficients.InfielderLateralSpeedFactor"/>）は中立値（効きが小さく得点だけ動いたので現状オフ・式は残す）。
/// 加速で間を抜けるゴロが増え（ゴロが内野を抜ける割合 2.0%→6.7%）、増えたぶんは内野安打の側を締めて得点帯を保った。</para>
/// </summary>
public sealed class InfielderReachModelTests
{
    private static Fielder Shortstop() => new(
        FieldPosition.Shortstop, new Vector3D(-9.0, 0, 40.0), new FielderAttributes { Fielding = 50, Speed = 50 });

    /// <summary>
    /// 本塁付近から遊撃手の定位置より三塁側（x≈-20.9）へ向かう強いゴロ（約38m/s＝137km/h）。
    /// 遊撃手の定位置からの距離は、強打球の一歩目の遅れ（既定値）を払うと間に合わず、
    /// 払わなければ（旧式）間に合う境界の当たり。
    /// </summary>
    private static BouncePath FastGrounderPastShortstop(FieldingCoefficients coeff, double targetX = -20.9)
    {
        var landing = new Vector3D(0.0, 0, 5.0);
        var dz = 40.0 - 5.0;
        var dx = targetX - landing.X;
        var scale = 38.0 / System.Math.Sqrt(dx * dx + dz * dz);
        var landingVelocity = new Vector3D(dx * scale, -3.0, dz * scale);
        return BouncePath.Compute(landing, landingVelocity, 0.20, coeff, IrregularBounce.None);
    }

    [Fact]
    public void InfielderCanCutOff_DefaultCoefficients_ShortstopCannotCutOffFastGrounder()
    {
        var coeff = new FieldingCoefficients();
        var path = FastGrounderPastShortstop(coeff);
        var fielders = new[] { Shortstop() };

        Assert.False(FieldingResolver.InfielderCanCutOff(fielders, path, coeff));
    }

    [Fact]
    public void InfielderCanCutOff_NeutralFastGrounderThreshold_ShortstopCutsOff()
    {
        // 強打球の一歩目の遅れを無効値（しきいを∞）へ戻すと、同じ境界の当たりが「抜けない」→「抜ける」へ反転する
        // ＝この境界が FastGrounderReactionThresholdMps 系で決まっていることの確認（回帰: この項を抜くと常にTrueへ戻る）。
        // 加速も中立（∞）にして、強打球の一歩目の遅れだけの効きを切り出す。
        var coeff = new FieldingCoefficients { FastGrounderReactionThresholdMps = 1_000_000.0, InfielderAccelMps2 = 1_000_000.0 };
        var path = FastGrounderPastShortstop(coeff);
        var fielders = new[] { Shortstop() };

        Assert.True(FieldingResolver.InfielderCanCutOff(fielders, path, coeff));
    }

    /// <summary>
    /// 加速（既定14m/s²）は遊撃手が詰められる横の範囲を狭める: 三塁側へ向かう強めのゴロ（強打球のしきい未満の
    /// 28m/s）の行き先を1mずつ振ると、加速ありで捕えられる行き先の数は加速なし（∞＝旧式）より少ない。
    /// </summary>
    [Fact]
    public void InfielderAccel_Default_NarrowsShortstopReach()
    {
        var withAccel = new FieldingCoefficients { FastGrounderReactionThresholdMps = 1_000_000.0 };
        var noAccel = withAccel with { InfielderAccelMps2 = 1_000_000.0 };
        Assert.Equal(14.0, new FieldingCoefficients().InfielderAccelMps2);
        var fielders = new[] { Shortstop() };

        int Cuts(FieldingCoefficients c)
        {
            var n = 0;
            for (var x = -30.0; x <= 10.0; x += 1.0)
            {
                var landing = new Vector3D(0.0, 0, 5.0);
                var dx = x - landing.X;
                var dz = 35.0;
                var scale = 28.0 / System.Math.Sqrt(dx * dx + dz * dz);
                var path = BouncePath.Compute(landing, new Vector3D(dx * scale, -3.0, dz * scale), 0.20, c, IrregularBounce.None);
                if (FieldingResolver.InfielderCanCutOff(fielders, path, c)) n++;
            }
            return n;
        }

        var a = Cuts(withAccel);
        var b = Cuts(noAccel);
        Assert.True(a < b, $"加速ありで捕えられる行き先 {a} が加速なし {b} より狭くなっていない");
        Assert.True(a > 0, "加速ありで1本も捕えられない＝正面のゴロまで抜けている");
    }

    /// <summary>
    /// 横移動の割引は既定で中立値（現状オフ）だが、式としては生きている＝値を入れれば効くことを確認する
    /// （将来の再校正のための回帰。中立値のまま埋もれて死んでいないか）。
    /// </summary>
    [Fact]
    public void InfielderCanCutOff_NonNeutralLateralFactor_ChangesOutcome_WhenDialedIn()
    {
        var fielders = new[] { Shortstop() };
        // 遊撃手の定位置からセンター寄りへ9m横一杯に詰める、強打球のしきい未満の緩いゴロ。
        var baseline = new FieldingCoefficients { FastGrounderReactionThresholdMps = 1_000_000.0 };
        var landing = new Vector3D(0.0, 0, 8.0);
        var landingVelocity = new Vector3D(0.4, -3.0, 24.0);
        var pathBaseline = BouncePath.Compute(landing, landingVelocity, 0.20, baseline, IrregularBounce.None);
        Assert.True(FieldingResolver.InfielderCanCutOff(fielders, pathBaseline, baseline), "前提: 横移動の割引なしでは捕れる");

        var withLateralPenalty = baseline with { InfielderLateralSpeedFactor = 0.5, InfielderAccelMps2 = 6.0 };
        var pathPenalty = BouncePath.Compute(landing, landingVelocity, 0.20, withLateralPenalty, IrregularBounce.None);
        Assert.False(FieldingResolver.InfielderCanCutOff(fielders, pathPenalty, withLateralPenalty));
    }

    /// <summary>
    /// 定位置のすぐ正面（守備範囲内）に来る緩いゴロは、3つの新係数の有無で結果が変わらない＝
    /// 新式は「走って詰める必要がある打球」だけに効き、普段の守備範囲内の処理には影響しない。
    /// </summary>
    [Fact]
    public void InfielderCanCutOff_RoutineBallWithinRadius_UnaffectedByNewCoefficients()
    {
        var defaultCoeff = new FieldingCoefficients();
        var neutralCoeff = new FieldingCoefficients
        {
            InfielderAccelMps2 = 1_000_000.0,
            InfielderLateralSpeedFactor = 1.0,
            FastGrounderReactionThresholdMps = 1_000_000.0,
        };
        var fielders = new[] { Shortstop() };
        // 遊撃手の定位置のほぼ正面・守備範囲内（4.2m）に収まる緩いゴロ。
        var landing = new Vector3D(-9.0, 0, 20.0);
        var landingVelocity = new Vector3D(0.0, -2.0, 15.0);

        var pathDefault = BouncePath.Compute(landing, landingVelocity, 0.20, defaultCoeff, IrregularBounce.None);
        var pathNeutral = BouncePath.Compute(landing, landingVelocity, 0.20, neutralCoeff, IrregularBounce.None);

        Assert.True(FieldingResolver.InfielderCanCutOff(fielders, pathDefault, defaultCoeff));
        Assert.True(FieldingResolver.InfielderCanCutOff(fielders, pathNeutral, neutralCoeff));
    }
}

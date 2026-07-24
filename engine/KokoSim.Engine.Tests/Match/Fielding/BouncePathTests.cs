using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 着地後のバウンド積分（Issue #63 / OPEN-QUESTIONS Q14）の物理検証。
/// 反発係数・摩擦から導かれる既知の値との突き合わせ（不変条件#1）。
/// </summary>
public sealed class BouncePathTests
{
    private static readonly FieldingCoefficients Coeff = new();
    private const double G = BouncePath.GravityMps2;

    /// <summary>
    /// 既知の値: 反発係数 e の接地では、連続する2つのバウンド頂点の比は e²。
    /// v⊥ が e 倍になり、頂点 h=v⊥²/2g は e² 倍になる（跳ね返りの基本則）。
    /// </summary>
    [Fact]
    public void ConsecutiveBounceApexRatio_EqualsRestitutionSquared()
    {
        var e = Coeff.BounceRestitution;
        // 前進10 m/s・落下12 m/s で接地（真下向き v⊥=12）。摩擦を消して鉛直だけを見る。
        var c = Coeff with { BounceFrictionMu = 0.0 };
        var path = BouncePath.Compute(
            new Vector3D(0, 0, 30), new Vector3D(0, -12, 10), 1.5, c, IrregularBounce.None);

        Assert.True(path.Hops.Count >= 2, $"弾みが2回未満（{path.Hops.Count}）");
        var a0 = path.Hops[0].ApexHeightM;
        var a1 = path.Hops[1].ApexHeightM;
        Assert.Equal(e * e, a1 / a0, 3);
    }

    /// <summary>既知の値: 第1バウンド頂点 = (e·|v⊥|)² / 2g。</summary>
    [Fact]
    public void FirstBounceApex_MatchesClosedForm()
    {
        var e = Coeff.BounceRestitution;
        var vPerp = 15.0;
        var path = BouncePath.Compute(
            new Vector3D(0, 0, 25), new Vector3D(0, -vPerp, 8), 1.2, Coeff, IrregularBounce.None);

        var expected = (e * vPerp) * (e * vPerp) / (2.0 * G);
        Assert.Equal(expected, path.Hops[0].ApexHeightM, 4);
    }

    /// <summary>反発が小さくなるほど弾みは低く、やがて転がりへ収束する（バウンド頂点の単調性）。</summary>
    [Fact]
    public void HigherRestitution_BouncesHigher()
    {
        var low = BouncePath.Compute(
            new Vector3D(0, 0, 30), new Vector3D(0, -14, 9), 1.5, Coeff with { BounceRestitution = 0.30 }, IrregularBounce.None);
        var high = BouncePath.Compute(
            new Vector3D(0, 0, 30), new Vector3D(0, -14, 9), 1.5, Coeff with { BounceRestitution = 0.60 }, IrregularBounce.None);

        Assert.True(high.MaxBounceApexM > low.MaxBounceApexM,
            $"高反発 {high.MaxBounceApexM:F2}m ≤ 低反発 {low.MaxBounceApexM:F2}m");
    }

    /// <summary>水平に近い当たりは接地で速度を保ち（滑って伸び）、真上から落ちる当たりは失速する。</summary>
    [Fact]
    public void FlatImpact_RetainsMoreHorizontalSpeedThanSteep()
    {
        var flat = BouncePath.Compute(
            new Vector3D(0, 0, 40), new Vector3D(0, -4, 30), 2.0, Coeff, IrregularBounce.None);
        var steep = BouncePath.Compute(
            new Vector3D(0, 0, 40), new Vector3D(0, -26, 8), 3.5, Coeff, IrregularBounce.None);

        Assert.True(flat.RollSpeedMps > steep.RollSpeedMps,
            $"水平 {flat.RollSpeedMps:F1} ≤ 大飛球 {steep.RollSpeedMps:F1}");
    }

    /// <summary>接地保持の下限（球の 5/7）を割らない: どんなに強い摩擦でも1接地で速度を全損しない。</summary>
    [Fact]
    public void HorizontalSpeed_NeverBelowRollingFloor_PerImpact()
    {
        var c = Coeff with { BounceFrictionMu = 5.0 }; // 非現実的に強い摩擦
        var vh0 = 20.0;
        var path = BouncePath.Compute(
            new Vector3D(0, 0, 20), new Vector3D(0, -10, vh0), 1.0, c, IrregularBounce.None);

        // 最初の弾みの水平速度は floor×vh0 を下回らない。
        Assert.True(path.Hops.Count > 0);
        Assert.True(path.Hops[0].HorizontalSpeedMps >= vh0 * c.BounceRollingRetention - 1e-6,
            $"1接地で {path.Hops[0].HorizontalSpeedMps:F2} まで落ちた（下限 {vh0 * c.BounceRollingRetention:F2}）");
    }

    /// <summary>位置は前進方向へ単調（後退しない）で、最後は停止点に収束する。</summary>
    [Fact]
    public void PositionIsMonotoneForward_AndSettles()
    {
        var path = BouncePath.Compute(
            new Vector3D(0, 0, 25), new Vector3D(0, -12, 14), 1.3, Coeff, IrregularBounce.None);

        var prev = -1.0;
        for (var t = path.HangTimeSeconds; t < path.SettleAtSeconds + 2.0; t += 0.05)
        {
            var d = path.PositionAt(t).Z; // 前進はほぼ +Z
            Assert.True(d >= prev - 1e-6, $"t={t:F2} で後退した");
            prev = d;
        }
        Assert.Equal(path.StopPosition.Z, path.PositionAt(path.SettleAtSeconds + 5.0).Z, 6);
    }

    /// <summary>イレギュラーバウンドは進行方向を横へずらす（決定論: 与えた角度どおり）。</summary>
    [Fact]
    public void IrregularBounce_DeflectsDirection()
    {
        var straight = BouncePath.Compute(
            new Vector3D(0, 0, 20), new Vector3D(0, -10, 12), 1.0, Coeff, IrregularBounce.None);
        var kicked = BouncePath.Compute(
            new Vector3D(0, 0, 20), new Vector3D(0, -10, 12), 1.0, Coeff, new IrregularBounce(true, 20.0, 1.0));

        Assert.True(Math.Abs(kicked.StopPosition.X) > Math.Abs(straight.StopPosition.X) + 1.0,
            "イレギュラーで横へ逸れていない");
        Assert.True(kicked.Irregular);
    }

    /// <summary>乱数を消費しない純関数（同入力＝同結果）。</summary>
    [Fact]
    public void Compute_IsDeterministic()
    {
        var a = BouncePath.Compute(new Vector3D(1, 0, 22), new Vector3D(2, -11, 13), 1.1, Coeff, IrregularBounce.None);
        var b = BouncePath.Compute(new Vector3D(1, 0, 22), new Vector3D(2, -11, 13), 1.1, Coeff, IrregularBounce.None);
        Assert.Equal(a.StopPosition.X, b.StopPosition.X, 9);
        Assert.Equal(a.StopPosition.Z, b.StopPosition.Z, 9);
        Assert.Equal(a.MaxBounceApexM, b.MaxBounceApexM, 9);
    }
}

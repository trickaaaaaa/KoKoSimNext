using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 表示用の投球飛翔パス（Issue #332）。端点は実データどおり・中間は物理由来の垂れを持つこと、
/// ホップ（誘発縦変化）が垂れを減らすことを、既知の値と突き合わせる。
/// </summary>
public sealed class PitchFlightPathTests
{
    private static readonly Vector3D Release = new(-0.4, 1.85, 16.74);
    private const double Flight = 0.45;

    /// <summary>端点は必ず実データを通る（弦の両端＝リリース点と実着弾点）。</summary>
    [Fact]
    public void Endpoints_MatchReleaseAndPlateLocation()
    {
        var start = PitchFlightPath.At(Release, 0.12, 0.85, Flight, 0.3, 0.0, 0.0);
        Assert.Equal(Release.X, start.X, 9);
        Assert.Equal(Release.Y, start.Y, 9);
        Assert.Equal(Release.Z, start.Z, 9);

        var end = PitchFlightPath.At(Release, 0.12, 0.85, Flight, 0.3, 0.0, Flight);
        Assert.Equal(0.12, end.X, 9);
        Assert.Equal(0.85, end.Y, 9);
        Assert.Equal(0.0, end.Z, 9);
    }

    /// <summary>範囲外の時刻はクランプする（コマ落ち・オーバーランで球が本塁を突き抜けない）。</summary>
    [Fact]
    public void TimeOutsideFlight_IsClamped()
    {
        var before = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.0, -1.0);
        var after = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.0, Flight + 1.0);
        Assert.Equal(Release.Y, before.Y, 9);
        Assert.Equal(0.9, after.Y, 9);
        Assert.Equal(0.0, after.Z, 9);
    }

    /// <summary>
    /// 無回転（ホップ0）は**重力だけの自由飛行そのもの**であること。端点を通る初速を逆算した
    /// <c>y = y0 + v0y·t − (g/2)t²</c> と全時刻で一致する＝これが式の正しさの決め手。
    /// 符号を逆にすると（弦より下へ膨らませると）ここが落ちる。
    /// </summary>
    [Fact]
    public void WithoutHop_MatchesFreeFallExactly()
    {
        const double plateY = 0.55;
        var v0y = (plateY - Release.Y) / Flight + PitchFlightPath.GravityMps2 / 2.0 * Flight;
        for (var i = 0; i <= 8; i++)
        {
            var t = Flight * i / 8.0;
            var exact = Release.Y + v0y * t - PitchFlightPath.GravityMps2 / 2.0 * t * t;
            var actual = PitchFlightPath.At(Release, 0.0, plateY, Flight, 0.0, 0.0, t);
            Assert.Equal(exact, actual.Y, 9);
        }
    }

    /// <summary>
    /// 球は**弦より上**を通る（重力で落ちる球は初速を上向きに打ち出すので上に凸）。
    /// 無回転の中点は弦より <c>g·T²/8</c>（T=0.45s で約25cm）上。
    /// </summary>
    [Fact]
    public void MidpointBowsAboveChord()
    {
        var mid = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.0, Flight / 2.0);
        var chordY = (Release.Y + 0.9) / 2.0;
        var expected = PitchFlightPath.GravityMps2 * Flight * Flight / 8.0;

        Assert.Equal(chordY + expected, mid.Y, 9);
        Assert.True(mid.Y > chordY, "弦より下を通っている（符号が逆＝全球がホップして見える）");
        Assert.InRange(expected, 0.20, 0.30);
    }

    /// <summary>
    /// **落差は広がっていく**（重力で加速するため）。符号を逆にすると落差が縮み、
    /// 後半が水平〜上昇して「あらゆる球がホップする」絵になる（2026-07-30 実害）。
    /// </summary>
    [Fact]
    public void DropPerStep_Grows_NeverRises()
    {
        const int n = 12;
        var ys = new double[n + 1];
        for (var i = 0; i <= n; i++)
            ys[i] = PitchFlightPath.At(Release, 0.0, 0.55, Flight, 0.30, 0.0, Flight * i / n).Y;

        for (var i = 1; i <= n; i++)
            Assert.True(ys[i] < ys[i - 1], $"コマ{i}で球が上がった（弾道の符号が逆）");
        for (var i = 2; i <= n; i++)
            Assert.True(ys[i - 1] - ys[i] > ys[i - 2] - ys[i - 1] - 1e-9,
                $"コマ{i}で落差が縮んだ（重力で加速していない＝ホップして見える）");
    }

    /// <summary>
    /// ホップは軌道を**弦へ寄せる**＝平坦にする（「落ちにくい」の正しい表現）。
    /// 上向きの力なのに弦から遠ざからないのは端点を固定しているため（式の説明参照）。
    /// </summary>
    [Fact]
    public void Hop_FlattensTowardChord()
    {
        var flat = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.0, Flight / 2.0);
        var hopping = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.40, 0.0, Flight / 2.0);
        var chordY = (Release.Y + 0.9) / 2.0;

        Assert.True(hopping.Y < flat.Y, "ホップがあるのに軌道が平坦になっていない");
        Assert.True(hopping.Y > chordY, "現実的なホップ量で弦より下へ抜けている");
        // 逸れは終点自由なら (a/2)T² = IVB。始点終点を固定した弦からの逸れはその 1/4（t=T/2 で t(T−t)=T²/4）
        Assert.Equal(flat.Y - 0.40 / 4.0, hopping.Y, 9);
    }

    /// <summary>
    /// engine が出す誘発縦変化の実用域では、必ず弦より上＝上に凸のままであること。
    /// 弦を割るのは <c>IVB > g·T²/2</c>（T=0.45s で約0.99m）で、実測の最大級0.5m の倍以上。
    /// </summary>
    [Fact]
    public void RealisticHopRange_StaysAboveChord()
    {
        var chordY = (Release.Y + 0.7) / 2.0;
        foreach (var ivb in new[] { 0.0, 0.15, 0.30, 0.45, 0.60 })
        {
            var mid = PitchFlightPath.At(Release, 0.0, 0.7, Flight, ivb, 0.0, Flight / 2.0);
            Assert.True(mid.Y > chordY, $"IVB={ivb} で弦より下へ抜けた");
        }
    }

    /// <summary>横変化も同じ規則（加速度と逆向きに膨らむ）。現モデルはバックスピン固定でほぼ0だが式は対称。</summary>
    [Fact]
    public void HorizontalBreak_BowsSideways()
    {
        var straight = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.0, Flight / 2.0);
        var breaking = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.20, Flight / 2.0);
        Assert.Equal(straight.X - 0.20 / 4.0, breaking.X, 9);
    }

    /// <summary>飛翔時間0（弾道なしの異常値）でも例外を出さず、着弾点に置く。</summary>
    [Fact]
    public void ZeroFlightTime_ReturnsPlatePosition()
    {
        var p = PitchFlightPath.At(Release, 0.2, 0.7, 0.0, 0.3, 0.0, 0.1);
        Assert.Equal(0.2, p.X, 9);
        Assert.Equal(0.7, p.Y, 9);
        Assert.Equal(0.0, p.Z, 9);
    }

    /// <summary>判定面までは延長版も同じ値（分岐で式が変わっていない）。</summary>
    [Fact]
    public void AtOrBeyond_BeforePlate_MatchesAt()
    {
        for (var i = 0; i <= 6; i++)
        {
            var t = Flight * i / 6.0;
            var plain = PitchFlightPath.At(Release, 0.1, 0.7, Flight, 0.3, 0.05, t);
            var beyond = PitchFlightPath.AtOrBeyond(Release, 0.1, 0.7, Flight, 0.3, 0.05, t);
            Assert.Equal(plain.X, beyond.X, 12);
            Assert.Equal(plain.Y, beyond.Y, 12);
            Assert.Equal(plain.Z, beyond.Z, 12);
        }
    }

    /// <summary>
    /// 延長は**接線**＝判定面の速度でまっすぐ。等間隔の時刻で等間隔に進み、判定面の通過点は動かない。
    /// <see cref="PitchFlightPath.At"/> の式を t&gt;T へ伸ばすと逸れの項の符号が反転して跳ね上がる。
    /// </summary>
    [Fact]
    public void AtOrBeyond_ExtendsAlongTangent()
    {
        var plate = PitchFlightPath.AtOrBeyond(Release, 0.12, 0.85, Flight, 0.3, 0.0, Flight);
        Assert.Equal(0.12, plate.X, 9);
        Assert.Equal(0.85, plate.Y, 9);
        Assert.Equal(0.0, plate.Z, 9);

        var v = PitchFlightPath.VelocityAtPlate(Release, 0.12, 0.85, Flight, 0.3, 0.0);
        for (var i = 1; i <= 3; i++)
        {
            var over = 0.005 * i;
            var p = PitchFlightPath.AtOrBeyond(Release, 0.12, 0.85, Flight, 0.3, 0.0, Flight + over);
            Assert.Equal(0.12 + v.X * over, p.X, 9);
            Assert.Equal(0.85 + v.Y * over, p.Y, 9);
            Assert.Equal(v.Z * over, p.Z, 9);
        }
    }

    /// <summary>判定面の速度は数値微分と一致すること（延長の向きが式の微分そのものである保証）。</summary>
    [Fact]
    public void VelocityAtPlate_MatchesNumericDerivative()
    {
        const double h = 1e-6;
        var a = PitchFlightPath.At(Release, 0.12, 0.85, Flight, 0.3, 0.08, Flight - h);
        var b = PitchFlightPath.At(Release, 0.12, 0.85, Flight, 0.3, 0.08, Flight);
        var v = PitchFlightPath.VelocityAtPlate(Release, 0.12, 0.85, Flight, 0.3, 0.08);
        Assert.Equal((b.X - a.X) / h, v.X, 3);
        Assert.Equal((b.Y - a.Y) / h, v.Y, 3);
        Assert.Equal((b.Z - a.Z) / h, v.Z, 3);
    }

    /// <summary>球はまだ落ちながらミットへ入る（延長区間で上向きに転じない）。</summary>
    [Fact]
    public void Extension_KeepsDescending_ForRealisticHop()
    {
        foreach (var ivb in new[] { 0.0, 0.3, 0.6 })
        {
            var v = PitchFlightPath.VelocityAtPlate(Release, 0.0, 0.8, Flight, ivb, 0.0);
            Assert.True(v.Y < 0, $"IVB={ivb} で判定面通過時に上昇している");
            Assert.True(v.Z < 0, "捕手側（z負）へ進んでいない");
        }
    }

    /// <summary>捕球面（本塁の 0.6m 後ろ）到達時刻は判定面のわずか後＝15ms級。</summary>
    [Fact]
    public void SecondsToDepth_ReachesCatchPlane_JustAfterPlate()
    {
        var t = PitchFlightPath.SecondsToDepth(Release, Flight, -0.6);
        Assert.InRange(t - Flight, 0.010, 0.020);

        var p = PitchFlightPath.AtOrBeyond(Release, 0.1, 0.7, Flight, 0.3, 0.0, t);
        Assert.Equal(-0.6, p.Z, 9);
    }

    /// <summary>判定面（z=0）の到達時刻は飛翔時間そのもの。</summary>
    [Fact]
    public void SecondsToDepth_AtPlate_EqualsFlightTime()
    {
        Assert.Equal(Flight, PitchFlightPath.SecondsToDepth(Release, Flight, 0.0), 12);
    }

    /// <summary>
    /// 実弾道（<see cref="PitchSimulator"/>）の飛翔時間と噛み合うこと。パスは弾道を再積分せず
    /// 端点＋観測量だけで組むが、飛翔時間は実測値をそのまま使う＝球の速さが物理と一致する。
    /// </summary>
    [Fact]
    public void UsesRealFlightTime_FromTrajectory()
    {
        var spec = new PitchSpec { SpeedKmh = 140.0, SpinRadPerSec = PitchSpec.BackspinFromRpm(2200) };
        var tr = PitchSimulator.Simulate(spec, new Aerodynamics(), new MoundGeometry());
        Assert.InRange(tr.FlightTimeSeconds, 0.35, 0.55);

        var end = PitchFlightPath.At(Release, 0.05, 0.8,
            tr.FlightTimeSeconds, tr.InducedVerticalBreakM, tr.InducedHorizontalBreakM, tr.FlightTimeSeconds);
        Assert.Equal(0.05, end.X, 9);
        Assert.Equal(0.8, end.Y, 9);
    }
}

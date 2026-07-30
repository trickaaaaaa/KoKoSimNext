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
    /// 無回転（ホップ0）の中点の垂れは重力だけで決まる＝<c>g·T²/8</c>。
    /// T=0.45s なら約25cmで、実際の投球が弦から垂れる量と同程度。
    /// </summary>
    [Fact]
    public void MidpointSag_WithoutHop_MatchesGravityOnly()
    {
        var mid = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.0, Flight / 2.0);
        var chordY = (Release.Y + 0.9) / 2.0;
        var expected = PitchFlightPath.GravityMps2 * Flight * Flight / 8.0;

        Assert.Equal(chordY - expected, mid.Y, 9);
        Assert.InRange(expected, 0.20, 0.30);
    }

    /// <summary>ホップは垂れを打ち消す（同一コース・同一飛翔時間なら、ホップが大きいほど中点は高い）。</summary>
    [Fact]
    public void Hop_ReducesSag()
    {
        var flat = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.0, Flight / 2.0);
        var hopping = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.40, 0.0, Flight / 2.0);
        Assert.True(hopping.Y > flat.Y, "ホップがあるのに垂れが減っていない");

        // 逸れは終点自由なら (a/2)T² = IVB。始点終点を固定した弦からの逸れはその 1/4（t=T/2 で t(T−t)=T²/4）
        Assert.Equal(flat.Y + 0.40 / 4.0, hopping.Y, 9);
    }

    /// <summary>横変化も同じ形で効く（現モデルは全球種バックスピン固定でほぼ0だが、入れば曲がる）。</summary>
    [Fact]
    public void HorizontalBreak_BowsSideways()
    {
        var straight = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.0, Flight / 2.0);
        var breaking = PitchFlightPath.At(Release, 0.0, 0.9, Flight, 0.0, 0.20, Flight / 2.0);
        Assert.Equal(straight.X + 0.20 / 4.0, breaking.X, 9);
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

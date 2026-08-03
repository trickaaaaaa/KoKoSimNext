using System.Globalization;
using System.Linq;
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
/// アンアシスト刺殺（3U, Issue #400）。一塁手が捕球点からベースを踏むほうが送球より速い当たりで、
/// 判定が <see cref="FieldingPlay.UnassistedPutout"/> を立て、表示（TimelineBuilder）が送球セグメントも
/// 投手カバーも生成せず一塁手自身が踏むことを固定する。判定は物理層の時間比較で決める（不変条件#1）。
///
/// 回帰の実害（修正前）: 内野ゴロの守備所要は必ず「捕球＋オーバーヘッド＋握り替え0.70＋送球」で解かれ、
/// 一塁手が処理してもベースの真横で握り替えを払っていた＝内野安打の判定が実際より遅い守備時間で解かれ、
/// 絵は常に「一塁手→投手カバー」へ送球していた（3U が一度も出ない）。
/// </summary>
public sealed class UnassistedPutoutTests
{
    private readonly ITestOutputHelper _out;
    public UnassistedPutoutTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();
    /// <summary>失策乱数を止め、判定を所要時間だけに絞る（InfieldGrounderTimingTests と同型）。</summary>
    private static readonly FieldingCoefficients Coeff = new() { ErrorBaseProb = 0, ThrowErrorBaseProb = 0 };

    private static FieldingPlay Grounder(double exitVelocityMps, double bearingDeg, double launchAngleDeg = 3.0)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = exitVelocityMps, LaunchAngleDeg = launchAngleDeg, BearingDeg = bearingDeg },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = 50, Speed = 50, ArmStrength = 50 }),
            Coeff, new Xoshiro256Random(777));

    private static double DistToFirst(FieldingPlay p)
        => System.Math.Sqrt(
            (Field.FirstBase.X - p.FieldedX!.Value) * (Field.FirstBase.X - p.FieldedX!.Value)
            + (Field.FirstBase.Z - p.FieldedZ!.Value) * (Field.FirstBase.Z - p.FieldedZ!.Value));

    // ===== 判定モデル（engine）: アンアシスト経路 =====

    [Fact]
    public void FirstBaseman_NearBag_TakesUnassisted_AndIsFasterThanThrowing()
    {
        // 一塁線寄りの弱いゴロ。一塁手が捕球点(≈18,23)＝ベースから約4mで捕る＝踏むほうが速い。
        var play = Grounder(exitVelocityMps: 22.0, bearingDeg: 38.0);
        var dist = DistToFirst(play);
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0} 捕球点({1:F1},{2:F1}) ベースまで{3:F2}m 捕球{4:F3}s→刺殺{5:F3}s 3U={6} {7}",
            play.FielderRole, play.FieldedX, play.FieldedZ, dist,
            play.FieldedAtSeconds, play.ThrowArriveSeconds, play.UnassistedPutout, play.Result));

        Assert.Equal(FieldPosition.FirstBase, play.FielderRole);
        Assert.True(play.UnassistedPutout, "ベース至近で捕った一塁手がアンアシストにならない（3U が出ない回帰）");
        Assert.Equal(BattedBallResult.Out, play.Result);

        // 刺殺時刻は「捕球＋オーバーヘッド＋捕球点→ベースを走り込む自重」＝握り替え・送球を払わない。
        var overheadPlusRun = play.ThrowArriveSeconds!.Value - play.FieldedAtSeconds!.Value;
        var sprint = new FielderAttributes { Speed = 50 }.SprintSpeedMps;
        var expectedRun = Coeff.InfieldPlayOverheadSeconds + dist / sprint;
        Assert.Equal(expectedRun, overheadPlusRun, 3);

        // 送球していたら払っていた握り替え＋送球の所要より、走り込みのほうが速いから採られている。
        var throwSpeed = new FielderAttributes { ArmStrength = 50 }.ThrowSpeedMps;
        var throwDelta = Coeff.InfieldPlayOverheadSeconds
                         + Coeff.ThrowTransferSeconds
                           * new FielderAttributes { Fielding = 50 }
                               .TransferFactor(Coeff.ThrowTransferFieldingSlope, Coeff.ThrowTransferFactorMin)
                         + dist / throwSpeed;
        Assert.True(overheadPlusRun < throwDelta,
            $"アンアシスト所要 {overheadPlusRun:F3}s が送球所要 {throwDelta:F3}s より速くない（比較が逆）");
    }

    [Fact]
    public void FirstBaseman_FieldingWideOfBag_StillThrows()
    {
        // 同じ一塁手でも一二塁間寄り（捕球点がベースから約7.7m）では、走るより送球が速い＝3-1 のまま。
        var play = Grounder(exitVelocityMps: 22.0, bearingDeg: 30.0);
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0} 捕球点({1:F1},{2:F1}) ベースまで{3:F2}m 3U={4} {5}",
            play.FielderRole, play.FieldedX, play.FieldedZ, DistToFirst(play), play.UnassistedPutout, play.Result));

        Assert.Equal(FieldPosition.FirstBase, play.FielderRole);
        Assert.True(DistToFirst(play) > 6.0, "この当たりはベースから遠い捕球点であること（テスト前提）");
        Assert.False(play.UnassistedPutout, "ベースから遠いのにアンアシストを選んでいる（送球のほうが速いはず）");
    }

    [Fact]
    public void OtherInfielders_NeverUnassisted()
    {
        // 三遊間・二塁手方向のゴロは一塁手以外が処理する＝アンアシストは一塁手専用なので常に false。
        foreach (var bearing in new[] { -30.0, -18.0, -6.0, 8.0, 18.0 })
        {
            var play = Grounder(exitVelocityMps: 32.0, bearingDeg: bearing);
            Assert.NotEqual(FieldPosition.FirstBase, play.FielderRole);
            Assert.False(play.UnassistedPutout, $"一塁手以外（{play.FielderRole}）がアンアシストになっている");
        }
    }

    [Fact]
    public void Unassisted_IsFasterThanTheOldForcedThrow_LowersDefenseTime()
    {
        // アンアシストが速いと選ばれた当たりは、旧モデル（必ず送球）より守備所要が短い＝内野安打が減る側へ動く。
        // 旧モデルの所要（握り替え＋送球）を同じ捕球点で組み直し、新モデルの刺殺時刻がそれ未満であることを固定する。
        var play = Grounder(exitVelocityMps: 20.0, bearingDeg: 40.0);
        Assert.True(play.UnassistedPutout, "テスト前提: この当たりはアンアシストになる");

        var dist = DistToFirst(play);
        var throwSpeed = new FielderAttributes { ArmStrength = 50 }.ThrowSpeedMps;
        var oldForcedThrowArrive = play.FieldedAtSeconds!.Value
                                   + Coeff.InfieldPlayOverheadSeconds
                                   + Coeff.ThrowTransferSeconds
                                     * new FielderAttributes { Fielding = 50 }
                                         .TransferFactor(Coeff.ThrowTransferFieldingSlope, Coeff.ThrowTransferFactorMin)
                                   + dist / throwSpeed;
        Assert.True(play.ThrowArriveSeconds!.Value < oldForcedThrowArrive,
            $"新モデルの刺殺 {play.ThrowArriveSeconds:F3}s が旧・強制送球 {oldForcedThrowArrive:F3}s 未満でない");
    }

    // ===== 表示モデル（TimelineBuilder）: 送球も投手カバーも積まない =====

    private static PlayTimeline BuildFrom(FieldingPlay play)
        => TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

    [Fact]
    public void Timeline_Unassisted_NoThrowSegment_NoPitcherCover_FirstBasemanStepsHimself()
    {
        // 手書きフィクスチャで表示側だけを固定する（捕球点≈ベース至近・UnassistedPutout=true）。
        var play = FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.FirstBase, fieldedX: 18.0, fieldedZ: 23.0,
            fieldedAtSeconds: 1.4, throwArriveSeconds: 2.1, unassistedPutout: true);
        var tl = BuildFrom(play);

        // 送球（山なりの Throw）は積まれない。
        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        // 代わりに保持して運ぶ Carry セグメントが捕球点→一塁へ入る（一塁手が持ったまま踏みに行く）。
        var carry = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Carry);
        Assert.Equal(play.FieldedX!.Value, carry.From.X, 6);
        Assert.Equal(play.FieldedZ!.Value, carry.From.Z, 6);
        Assert.Equal(Field.FirstBase.X, carry.To.X, 6);
        Assert.Equal(Field.FirstBase.Z, carry.To.Z, 6);
        Assert.Equal(FieldPosition.FirstBase, carry.FromRole);
        Assert.Equal(FieldPosition.FirstBase, carry.ToRole);

        // 投手はカバーに入らない（一塁ベースへ向かう移動があるのは一塁手だけ）。
        var first = new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z);
        var coverers = tl.Moves.Where(m => Near(m.To, first)).Select(m => m.Role).Distinct().ToList();
        Assert.DoesNotContain(FieldPosition.Pitcher, coverers);
        Assert.Contains(FieldPosition.FirstBase, coverers);
        // 一塁手のカバー移動は捕球後に始まる（捕球点でボールを持ってからベースへ運ぶ）。
        var cover = Assert.Single(tl.Moves, m => m.Role == FieldPosition.FirstBase && Near(m.To, first));
        Assert.Equal(FielderTask.CoverBase, cover.Task);
        Assert.True(cover.T0 >= 0.85 + play.FieldedAtSeconds!.Value - 1e-6, "カバー移動が捕球より前に始まっている");
    }

    [Fact]
    public void Timeline_EndToEnd_ResolvedUnassisted_HasNoThrow()
    {
        // 判定→表示の一気通貫（フィクスチャに頼らず、リゾルバが立てた UnassistedPutout がそのまま絵に出る）。
        var play = Grounder(exitVelocityMps: 22.0, bearingDeg: 38.0);
        Assert.True(play.UnassistedPutout, "テスト前提: リゾルバがアンアシストを立てる");
        var tl = BuildFrom(play);

        Assert.DoesNotContain(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        Assert.Contains(tl.Ball, b => b.Kind == BallSegmentKind.Carry);
        var first = new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z);
        var coverers = tl.Moves.Where(m => Near(m.To, first)).Select(m => m.Role).Distinct().ToList();
        Assert.DoesNotContain(FieldPosition.Pitcher, coverers);
    }

    private static bool Near(TimelinePoint a, TimelinePoint b)
        => System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)) < 1.5;
}

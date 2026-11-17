using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 走者ありの外野ゴロを走りながら掬う（issue #1079）: 移動の速さの付け方（<see cref="MoveProfile"/>）と
/// <see cref="TimelineBuilder.ApplyOutfieldRunThrough"/>。どれも表示専用で、端点の時刻・位置は変えない。
/// </summary>
public sealed class OutfieldRunThroughTests
{
    private static double Speed(Func<double, double> ease, double u, double h = 1e-5)
        => (ease(Math.Min(1, u + h)) - ease(Math.Max(0, u - h))) / (Math.Min(1, u + h) - Math.Max(0, u - h));

    [Theory]
    [InlineData(MoveProfile.Trapezoid)]
    [InlineData(MoveProfile.RunIn)]
    [InlineData(MoveProfile.RunOut)]
    public void Ease_EndpointsAreUnchanged_AndMonotonic(MoveProfile p)
    {
        Assert.Equal(0.0, PlaybackEvaluator.MoveEase(0, p));
        Assert.Equal(1.0, PlaybackEvaluator.MoveEase(1, p));
        var prev = 0.0;
        for (var u = 0.0; u <= 1.0; u += 0.01)
        {
            var v = PlaybackEvaluator.MoveEase(u, p);
            Assert.True(v >= prev - 1e-12, $"{p}: 進みが戻った u={u:F2}");
            prev = v;
        }
    }

    [Fact]
    public void Trapezoid_IsTheOldEase()
    {
        for (var u = 0.0; u <= 1.0; u += 0.05)
            Assert.Equal(PlaybackEvaluator.MoveEase(u), PlaybackEvaluator.MoveEase(u, MoveProfile.Trapezoid));
    }

    [Theory]
    [InlineData(MoveProfile.Trapezoid, 0.0)]
    [InlineData(MoveProfile.RunIn, 1.0)]     // 終点で巡航速度のまま
    [InlineData(MoveProfile.RunOut, 0.0)]    // 終点で止まる
    public void Ease_EndSpeed_MatchesProfile(MoveProfile p, double endSpeedOverPeak)
    {
        var end = Speed(u => PlaybackEvaluator.MoveEase(u, p), 1.0 - 1e-4);
        Assert.Equal(endSpeedOverPeak * PlaybackEvaluator.PeakSpeedFactor(p), end, 2);
    }

    [Fact]
    public void RunOut_StartsAtPeakSpeed()
    {
        var start = Speed(u => PlaybackEvaluator.MoveEase(u, MoveProfile.RunOut), 1e-4);
        Assert.Equal(PlaybackEvaluator.PeakSpeedFactor(MoveProfile.RunOut), start, 2);
    }

    private static PlayTimeline Grounder(double fieldedAt, double legT0)
    {
        var fieldPt = new TimelinePoint(-18.5, 71.7);
        return new PlayTimeline
        {
            Ball = new List<BallSegment>
            {
                new() { Kind = BallSegmentKind.Roll, T0 = 0.85, T1 = fieldedAt, From = new TimelinePoint(0, 0), To = fieldPt },
            },
            Moves = new List<FielderMove>
            {
                new() { Role = FieldPosition.LeftField, T0 = legT0, T1 = fieldedAt, To = fieldPt, Task = FielderTask.FieldBall },
                new() { Role = FieldPosition.CenterField, T0 = legT0, T1 = fieldedAt, To = new TimelinePoint(-8, 81), Task = FielderTask.Backup },
            },
            FielderStarts = new Dictionary<FieldPosition, TimelinePoint>
            {
                [FieldPosition.LeftField] = new TimelinePoint(-28, 80),
                [FieldPosition.CenterField] = new TimelinePoint(0, 85),
            },
            Result = "テスト", Duration = 6.0, ResolvedAt = 6.0,
        };
    }

    [Fact]
    public void RunThrough_KeepsCatchTimeAndPoint_AndCarriesWithContinuousSpeed()
    {
        const double fieldedAt = 3.8, release = 4.98;
        var tl = Grounder(fieldedAt, 1.2);
        var r = TimelineBuilder.ApplyOutfieldRunThrough(tl, FieldPosition.LeftField, new TimelinePoint(-18.5, 71.7), fieldedAt, release);
        Assert.NotNull(r);
        var (after, throwFrom) = r!.Value;

        var lf = after.Moves.Where(m => m.Role == FieldPosition.LeftField).OrderBy(m => m.T0).ToList();
        Assert.Equal(2, lf.Count);
        Assert.Equal(MoveProfile.RunIn, lf[0].Profile);
        Assert.Equal(fieldedAt, lf[0].T1);                       // 捕球の時刻・位置は不変
        Assert.Equal(-18.5, lf[0].To.X); Assert.Equal(71.7, lf[0].To.Z);
        Assert.Equal(MoveProfile.RunOut, lf[1].Profile);
        Assert.Equal(fieldedAt, lf[1].T0);
        Assert.True(lf[1].T1 <= release + 1e-9, "投げ始めより後まで運んでいる");
        Assert.Equal(lf[1].To, throwFrom);                       // 送球は運んで止まった点から

        // 捕球の前後で速さが途切れない（RunIn の終わりの速さ＝RunOut の始めの速さ）
        static double Dist(TimelinePoint a, TimelinePoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
        var vIn = Dist(new TimelinePoint(-28, 80), lf[0].To) / (lf[0].T1 - lf[0].T0) * PlaybackEvaluator.PeakSpeedFactor(MoveProfile.RunIn);
        var vOut = Dist(lf[0].To, lf[1].To) / (lf[1].T1 - lf[1].T0) * PlaybackEvaluator.PeakSpeedFactor(MoveProfile.RunOut);
        Assert.Equal(vIn, vOut, 6);

        // 他の野手の移動は1ビットも変えない
        Assert.Equal(tl.Moves.Single(m => m.Role == FieldPosition.CenterField),
            after.Moves.Single(m => m.Role == FieldPosition.CenterField));
    }

    [Fact]
    public void RunThrough_SkipsWhenTheFielderBarelyMoves()
    {
        const double fieldedAt = 3.8;
        var tl = Grounder(fieldedAt, 1.2) with
        {
            FielderStarts = new Dictionary<FieldPosition, TimelinePoint> { [FieldPosition.LeftField] = new TimelinePoint(-19.5, 72.5) },
        };
        Assert.Null(TimelineBuilder.ApplyOutfieldRunThrough(tl, FieldPosition.LeftField, new TimelinePoint(-18.5, 71.7), fieldedAt, 4.98));
    }
}

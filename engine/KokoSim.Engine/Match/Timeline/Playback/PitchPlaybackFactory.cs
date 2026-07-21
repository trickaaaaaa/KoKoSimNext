using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// 打球にならない投球（見逃し・ボール・空振り・ファウル）を2D俯瞰で見せるための、
/// 「投球1球ぶんだけ」の再生プレーを組む供給口（描画専用・試合結果には一切影響しない）。
///
/// セグメントは <see cref="PitchSegment"/> ただ1本（マウンド→本塁）。捕手からの返球（本塁→マウンド）は
/// 描画対象外なので送球セグメントは積まない。野手は定位置のまま（Moves 空）で、占有塁の走者だけを
/// その塁に静止表示する（打席前の塁状況をそのまま残す）。
/// </summary>
public static class PitchPlaybackFactory
{
    /// <summary>投球1球ぶんの再生時間[秒]（＝<see cref="PitchSegment"/> の所要時間）。</summary>
    public const double PitchDurationSeconds = PitchSegment.DurationSeconds;

    /// <summary>
    /// 投球1球ぶんの再生プレーを組む。first/second/third=打席前の塁占有（静止走者として表示する）。
    /// </summary>
    public static PlaybackPlay PitchOnly(bool first, bool second, bool third)
    {
        var runners = new List<PlaybackRunner>();
        if (third) runners.Add(StillRunner(FieldDiagramGeometry.Third));
        if (second) runners.Add(StillRunner(FieldDiagramGeometry.Second));
        if (first) runners.Add(StillRunner(FieldDiagramGeometry.First));

        return new PlaybackPlay
        {
            Name = "投球",
            Dur = PitchDurationSeconds,
            Result = "",
            ResAt = double.PositiveInfinity,   // 結果チップは投球フェーズでは出さない
            Ball = new PlaybackBallSegment[] { new PitchSegment(0) },
            Runners = runners,
        };
    }

    private static PlaybackRunner StillRunner((double X, double Y) at)
    {
        var p = new PlaybackVec(at.X, at.Y);
        return new PlaybackRunner
        {
            Label = "走",
            Segs = new[] { new PlaybackRun(0, PitchDurationSeconds, p, p) },
        };
    }
}

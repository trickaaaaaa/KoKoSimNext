using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// エンジン実出力 <see cref="PlayTimeline"/>（TimelineBuilder の端点モデル：From→To＋ApexHeightM）を、
/// 試合2D俯瞰ビューが再生する <see cref="PlaybackPlay"/> へ変換するアダプタ。
///
/// 変換はこのアダプタ側で完結し、**ビュー（Match2DPlaybackElement/MatchDetailController）と
/// 忠実移植モデル（PlaybackModel/PlaybackSamples）には一切手を入れない**。
/// ボール軌道は端点補間の <see cref="EndpointBallSegment"/> で表す（種別ごとに高さプロファイルを付ける）。
///
/// 座標規約は両者一致（本塁原点・+X=一塁側・前方=センター）。PlayTimeline は +Z=センター、
/// PlaybackVec は +Y=センターで、同一平面のため <c>(X, Z) → (X, Y)</c> の直写し。
/// </summary>
public static class PlayTimelineAdapter
{
    public static PlaybackPlay ToPlaybackPlay(PlayTimeline t)
    {
        if (t is null) throw new ArgumentNullException(nameof(t));

        var ball = t.Ball.Select(EndpointBallSegment.From).Cast<PlaybackBallSegment>().ToArray();

        // 野手の移動を Role でまとめる（同一野手の複数移動は T0 順）。
        var moves = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>();
        foreach (var grp in t.Moves.GroupBy(m => m.Role))
        {
            moves[grp.Key] = grp
                .OrderBy(m => m.T0)
                .Select(m => new PlaybackMove(m.T0, m.T1, Vec(m.To)))
                .ToArray();
        }

        // 走者をラベル（"打"/"走1"/…＝エンジンが一意付与）でまとめる。アウトのレッグ終端で hideAt。
        var runners = new List<PlaybackRunner>();
        foreach (var grp in t.Runners.GroupBy(r => r.Label))
        {
            var legs = grp.OrderBy(r => r.T0).ToList();
            double? hideAt = null;
            foreach (var leg in legs)
                if (leg.OutAtEnd) hideAt = leg.T1;   // 憤死/封殺の瞬間に消える（mock hideAt と同意味）
            runners.Add(new PlaybackRunner
            {
                Label = grp.Key,
                Segs = legs.Select(l => new PlaybackRun(l.T0, l.T1, Vec(l.From), Vec(l.To))).ToArray(),
                HideAt = hideAt,
            });
        }

        var caps = t.Captions.Select(c => new PlaybackCaption(c.T, c.Text)).ToArray();

        return new PlaybackPlay
        {
            Name = t.Result,
            Dur = t.Duration,
            Result = t.Result,
            ResAt = t.ResolvedAt,
            Ball = ball,
            Moves = moves,
            Runners = runners,
            Caps = caps,
        };
    }

    /// <summary>TimelinePoint(X, Z=センター) → PlaybackVec(X, Y=センター)。同一平面の直写し。</summary>
    private static PlaybackVec Vec(TimelinePoint p) => new(p.X, p.Z);
}

/// <summary>
/// エンジンの端点ボールセグメント（From→To＋種別）を線形補間で再生する PlaybackBallSegment。
/// XY は From→To を等速補間、高さ h は種別ごとのプロファイル（投球=落下／滞空=放物線 頂点ApexHeightM／
/// ゴロ=地這い／送球=山なり／保持=低め）。エンジンが持たない中間の物理は演出として最小限に補う。
/// </summary>
public sealed record EndpointBallSegment : PlaybackBallSegment
{
    private readonly BallSegmentKind _kind;
    private readonly double _fx, _fy, _tx, _ty, _apex, _dist;

    private EndpointBallSegment(BallSegmentKind kind, double t0, double t1, TimelinePoint from, TimelinePoint to, double apex)
        : base(t0, t1)
    {
        _kind = kind;
        _fx = from.X; _fy = from.Z;
        _tx = to.X; _ty = to.Z;
        _apex = apex;
        var dx = _tx - _fx;
        var dy = _ty - _fy;
        _dist = Math.Sqrt(dx * dx + dy * dy);
    }

    public static EndpointBallSegment From(BallSegment s)
        => new(s.Kind, s.T0, s.T1, s.From, s.To, s.ApexHeightM);

    public override PlaybackBall At(double tt)
    {
        var dur = T1 - T0;
        var u = dur > 0 ? tt / dur : 0.0;
        var x = _fx + (_tx - _fx) * u;
        var y = _fy + (_ty - _fy) * u;
        var h = Height(u);
        return new PlaybackBall(x, y, h);
    }

    // 種別ごとの高さプロファイル[m]（表示演出。判定・帯には無関係）。
    private double Height(double u)
    {
        switch (_kind)
        {
            case BallSegmentKind.Pitch:
                return 1.7 - 1.1 * u;                          // リリース→やや低め（mock pitch）
            case BallSegmentKind.Flight:
                return Math.Max(0, _apex) * 4 * u * (1 - u);   // 0→頂点ApexHeightM→0 の放物線
            case BallSegmentKind.Throw:
                return 1.7 + (_dist / 9) * 4 * u * (1 - u);    // 山なりの送球（mock throw）
            case BallSegmentKind.Carry:
                return 0.6;                                    // 野手が保持して移動（低め）
            case BallSegmentKind.Roll:
            default:
                return 0.0;                                    // 地を這うゴロ
        }
    }
}

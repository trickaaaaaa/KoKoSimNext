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
            // 頭上フルネームラベル用の本人（issue #393）。同じ Label のレッグ群は同一人物のはず＝先頭のを使う。
            var runnerIdentity = DuelPlayerIdentity.Of(legs.Find(l => l.Runner is not null)?.Runner);
            runners.Add(new PlaybackRunner
            {
                Label = grp.Key,
                Segs = legs.Select(l => new PlaybackRun(l.T0, l.T1, Vec(l.From), Vec(l.To))
                    { OutAtEnd = l.OutAtEnd, CloseCall = l.CloseCall, Force = l.Force, Returning = l.Returning }).ToArray(),
                HideAt = hideAt,
                Identity = runnerIdentity,
            });
        }

        var caps = t.Captions.Select(c => new PlaybackCaption(c.T, c.Text)).ToArray();
        var judgements = t.Judgements
            .Select(j => new PlaybackJudgement(j.T, j.Kind, j.CloseCall, j.Label, j.DistanceM)).ToArray();

        var play = new PlaybackPlay
        {
            Name = t.Result,
            Dur = t.Duration,
            Result = t.Result,
            ResAt = t.ResolvedAt,
            Ball = ball,
            Moves = moves,
            // 出発点（判定が距離を測った守備配置）。座標と違って推定できないので素通しする
            Starts = t.FielderStarts?.ToDictionary(kv => kv.Key, kv => Vec(kv.Value)),
            Runners = runners,
            // 守備の芝居の入力（表示専用）。座標と同じく既に決まった値の素通し
            CatchMargin = t.CatchMarginSeconds,
            Fumbled = t.FumbledCatch,
            Caps = caps,
            Judgements = judgements,
        };

        // 結果キャプション（TimelineBuilder が「決着時刻・結果テキスト」で積む実況の最終行）を
        // 表示解決時刻へ動かす（issue #438）: 打者がセーフで生きる結果は到達の絵より先に
        // 「レフト前ヒット」等を出さない。時刻の計算は PlaybackDisplaySync が単一ソース。
        // 対象は「T=決着 かつ 本文=結果テキスト」の行だけ＝中間実況（○○が捕った 等）は動かさない。
        var showAt = PlaybackDisplaySync.ResultShowTime(play);
        if (showAt > t.ResolvedAt)
        {
            play = play with
            {
                // ⚠ 時刻順の安定ソートを掛け直す。CaptionAt は**リスト順**で上書きするので、
                // 結果行だけ後ろの時刻へ動かすと、リスト上それより後ろの追記実況
                // （本塁クロスの「還った！」等）が最終表示に勝ち続けてしまう。
                Caps = caps.Select(c =>
                        Math.Abs(c.T - t.ResolvedAt) < 1e-9 && c.Text == t.Result
                            ? new PlaybackCaption(showAt, c.Text)
                            : c)
                    .OrderBy(c => c.T)
                    .ToArray(),
            };
        }
        return play;
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
    /// <summary>エンジン側の種別（投球/滞空/転がり/送球/保持/バウンド）。カメラ演出等の判定に使う（Issue #258）。</summary>
    public BallSegmentKind Kind => _kind;

    private readonly BallSegmentKind _kind;
    private readonly double _fx, _fy, _tx, _ty, _apex, _launch, _dist;

    private EndpointBallSegment(
        BallSegmentKind kind, double t0, double t1, TimelinePoint from, TimelinePoint to, double apex, double launch)
        : base(t0, t1)
    {
        _kind = kind;
        _fx = from.X; _fy = from.Z;
        _tx = to.X; _ty = to.Z;
        _apex = apex;
        _launch = Math.Max(0.0, launch);
        var dx = _tx - _fx;
        var dy = _ty - _fy;
        _dist = Math.Sqrt(dx * dx + dy * dy);
    }

    public static EndpointBallSegment From(BallSegment s)
        => new(s.Kind, s.T0, s.T1, s.From, s.To, s.ApexHeightM, s.LaunchHeightM)
        {
            // 送球の投げ手／受け手（Issue E1）。座標と違って**推定できない**値なので素通しする
            FromRole = s.FromRole, ToRole = s.ToRole, ToBase = s.ToBase,
        };

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
                // 打点→頂点ApexHeightM→着地0 の放物線（issue duel-view-fly-leaves-bat-flat-then-rises）。
                // 線形項 launch·(1−u) で打点から始め、放物線項の振幅を launch/2 だけ削ると頂点はほぼ ApexHeightM
                // のまま（誤差 launch²/(16·(Apex−launch/2))＝1mの打点・20mの弧で 0.3cm）。launch=0 なら従来式。
                return _launch * (1 - u) + Math.Max(0, _apex - _launch * 0.5) * 4 * u * (1 - u);
            case BallSegmentKind.Throw:
                return 1.7 + (_dist / 9) * 4 * u * (1 - u);    // 山なりの送球（mock throw）
            case BallSegmentKind.Carry:
                return 0.6;                                    // 野手が保持して移動（低め）
            case BallSegmentKind.Bounce:
                // バウンド（Issue #63）: 減衰しながら数回弾む。頂点は ApexHeightM、区間を通じて 3 山＋減衰。
                return Math.Max(0.0, _apex) * Math.Abs(Math.Sin(Math.PI * 3 * u)) * (1.0 - 0.6 * u);
            case BallSegmentKind.Roll:
            default:
                return 0.0;                                    // 地を這うゴロ
        }
    }
}

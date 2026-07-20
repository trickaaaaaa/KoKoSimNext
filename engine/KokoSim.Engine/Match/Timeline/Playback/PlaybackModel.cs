using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline.Playback;

// ─────────────────────────────────────────────────────────────────────────────
// 試合2D俯瞰ビューの「再生用」データモデル＋補間関数（純C#・UnityEngine非依存）。
//
// これは docs/design/mock-match-2d-view.html の PLAYS 配列・補間関数の忠実移植であり、
// 式・定数・データ構造を一切変えずに写経したもの（設計者Claude指示 Step 1）。
// 既存の PlayTimeline（TimelineBuilder の出力契約・From/To 端点方式）とは別系統で、
// 将来 PlayTimeline → この PlaybackPlay への変換アダプタを別タスクで作る想定。
//
// セグメント語彙は6種で固定: pitch / arc / roll / throw（ボール）・move（野手）・run（走者）。
// 座標規約はモックの [x,y] と同一（本塁原点・+X=一塁側・+Y=センター方向）。
// 時刻 t を与えると全アクターの位置を返す純関数群（PlaybackEvaluator）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>平面座標（モックの [x,y] と同一規約, 単位[m]）。</summary>
public readonly record struct PlaybackVec(double X, double Y);

/// <summary>ボール位置（x,y と高さ h[m]。影と本体の分離＝高さ表現に使う）。</summary>
public readonly record struct PlaybackBall(double X, double Y, double H);

/// <summary>ボール軌道1セグメントの基底（誰が・どこから・どこへ・何時に）。<br/>
/// <see cref="At"/> は「セグメント開始からのローカル時刻 tt」を受ける（mock: s.at(t - s.t0)）。</summary>
public abstract record PlaybackBallSegment(double T0, double T1)
{
    public abstract PlaybackBall At(double tt);
}

/// <summary>投球（マウンド→本塁）。mock: pitchSeg。</summary>
public sealed record PitchSegment(double StartT) : PlaybackBallSegment(StartT, StartT + 0.45)
{
    public override PlaybackBall At(double tt)
    {
        var u = tt / 0.45;
        return new PlaybackBall(0, 17.4 - 16.6 * u, 1.7 - 1.1 * u);
    }
}

/// <summary>放物線（滞空。tEnd 指定で捕球打ち切り）。mock: arcSeg。</summary>
public sealed record ArcSegment : PlaybackBallSegment
{
    private readonly double _x0, _y0, _h0, _vx, _vy, _vz;

    public ArcSegment(double t0, double x0, double y0, double h0, double vx, double vy, double vz, double? tEnd = null)
        : base(t0, t0 + Dur(h0, vz, tEnd))
    {
        _x0 = x0; _y0 = y0; _h0 = h0; _vx = vx; _vy = vy; _vz = vz;
    }

    private static double Dur(double h0, double vz, double? tEnd)
    {
        var tg = (vz + Math.Sqrt(vz * vz + 2 * 9.8 * h0)) / 9.8;
        return Math.Min(tEnd ?? tg, tg);
    }

    public override PlaybackBall At(double tt)
        => new(_x0 + _vx * tt, _y0 + _vy * tt, Math.Max(0, _h0 + _vz * tt - 4.9 * tt * tt));
}

/// <summary>ゴロ/バウンド（減速転がり＋任意のhopバウンド）。mock: rollSeg。</summary>
public sealed record RollSegment : PlaybackBallSegment
{
    private readonly double _x0, _y0, _dx, _dy, _v0, _a, _hop;

    public RollSegment(double t0, double x0, double y0, double dx, double dy, double v0, double a, double dur, double hop)
        : base(t0, t0 + dur)
    {
        _x0 = x0; _y0 = y0; _dx = dx; _dy = dy; _v0 = v0; _a = a; _hop = hop;
    }

    public override PlaybackBall At(double tt)
    {
        var d = Math.Max(0, _v0 * tt + 0.5 * _a * tt * tt);
        var h = _hop != 0 ? Math.Max(0, _hop * Math.Abs(Math.Sin(6 * tt)) * Math.Exp(-1.4 * tt)) : 0;
        return new PlaybackBall(_x0 + _dx * d, _y0 + _dy * d, h);
    }
}

/// <summary>送球（野手→野手/塁）。mock: throwSeg。</summary>
public sealed record ThrowSegment : PlaybackBallSegment
{
    private readonly double _fx, _fy, _dx, _dy, _dist;

    public ThrowSegment(double t0, PlaybackVec from, PlaybackVec to, double spd)
        : base(t0, t0 + Dist(from, to) / spd)
    {
        _fx = from.X; _fy = from.Y;
        _dx = to.X - from.X; _dy = to.Y - from.Y;
        _dist = Dist(from, to);
    }

    private static double Dist(PlaybackVec from, PlaybackVec to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override PlaybackBall At(double tt)
    {
        var dur = T1 - T0;
        var u = tt / dur;
        return new PlaybackBall(_fx + _dx * u, _fy + _dy * u, 1.7 + (_dist / 9) * 4 * u * (1 - u));
    }
}

/// <summary>野手1人の移動（t0→t1 で現在地→to へ easeIO 補間）。mock: move。</summary>
public sealed record PlaybackMove(double T0, double T1, PlaybackVec To);

/// <summary>走者1人の塁間レッグ（t0→t1 で from→to）。mock: run。</summary>
public sealed record PlaybackRun(double T0, double T1, PlaybackVec From, PlaybackVec To);

/// <summary>走者（複数レッグ＋任意の消滅時刻 hideAt＋表示ラベル）。mock: runners[i]。</summary>
public sealed record PlaybackRunner
{
    public string Label { get; init; } = "打";
    public required IReadOnlyList<PlaybackRun> Segs { get; init; }
    public double? HideAt { get; init; }
}

/// <summary>時刻付き実況キャプション。mock: caps[i] = [t, text]。</summary>
public readonly record struct PlaybackCaption(double T, string Text);

/// <summary>1プレーの再生データ（mock: PLAYS[i]）。</summary>
public sealed record PlaybackPlay
{
    public required string Name { get; init; }
    public required double Dur { get; init; }
    public required string Result { get; init; }
    public required double ResAt { get; init; }
    public required IReadOnlyList<PlaybackBallSegment> Ball { get; init; }
    public IReadOnlyDictionary<FieldPosition, IReadOnlyList<PlaybackMove>> Moves { get; init; }
        = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>();
    public IReadOnlyList<PlaybackRunner> Runners { get; init; } = Array.Empty<PlaybackRunner>();
    public IReadOnlyList<PlaybackCaption> Caps { get; init; } = Array.Empty<PlaybackCaption>();
}

/// <summary>
/// 再生評価器（純関数群）。mock: ballAt / fielderAt / runnerAt / easeIO の忠実移植。
/// 時刻 t を与えると各アクターの位置を返す（描画はこの出力を写すだけ）。
/// </summary>
public static class PlaybackEvaluator
{
    /// <summary>野手9人の初期守備位置[m]。地理は共通ソース <see cref="FieldDiagramGeometry.MatchFielderPosition"/>（mock DEF）。</summary>
    public static readonly IReadOnlyDictionary<FieldPosition, PlaybackVec> DefaultPositions = BuildDefaults();

    private static IReadOnlyDictionary<FieldPosition, PlaybackVec> BuildDefaults()
    {
        var d = new Dictionary<FieldPosition, PlaybackVec>();
        foreach (var pos in new[]
                 {
                     FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase,
                     FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop,
                     FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
                 })
        {
            var (x, y) = FieldDiagramGeometry.MatchFielderPosition(pos);
            d[pos] = new PlaybackVec(x, y);
        }
        return d;
    }

    /// <summary>mock: easeIO。smoothstep（区間外はクランプ）。</summary>
    public static double EaseIO(double u) => u < 0 ? 0 : u > 1 ? 1 : u * u * (3 - 2 * u);

    /// <summary>時刻 t のボール位置。未投球なら直前の保持位置、セグメント間は最後の位置で保持。mock: ballAt。</summary>
    public static PlaybackBall? BallAt(PlaybackPlay play, double t)
    {
        PlaybackBall? held = null;
        foreach (var s in play.Ball)
        {
            if (t < s.T0) return held;               // まだ投げていない → 直前の保持位置
            if (t <= s.T1) return s.At(t - s.T0);
            held = s.At(s.T1 - s.T0);                // セグメント間は最後の位置で保持
        }
        return held;
    }

    /// <summary>時刻 t の野手位置と、移動中フラグ（黄枠ハイライト用）。mock: fielderAt。</summary>
    public static (PlaybackVec Pos, bool Moving) FielderAt(PlaybackPlay play, FieldPosition key, double t)
    {
        var pos = DefaultPositions[key];
        var moving = false;
        if (play.Moves != null && play.Moves.TryGetValue(key, out var segs))
        {
            foreach (var m in segs)
            {
                if (t <= m.T0) break;
                var u = EaseIO((t - m.T0) / (m.T1 - m.T0));
                pos = new PlaybackVec(pos.X + (m.To.X - pos.X) * u, pos.Y + (m.To.Y - pos.Y) * u);
                if (t < m.T1) moving = true;
            }
        }
        return (pos, moving);
    }

    /// <summary>時刻 t の走者位置（hideAt 経過後は null＝非表示）。mock: runnerAt。</summary>
    public static PlaybackVec? RunnerAt(PlaybackRunner r, double t)
    {
        if (r.HideAt.HasValue && t > r.HideAt.Value) return null;
        PlaybackVec? pos = null;
        foreach (var s in r.Segs)
        {
            if (t < s.T0) return pos;
            var u = Math.Min(1, (t - s.T0) / (s.T1 - s.T0));
            pos = new PlaybackVec(s.From.X + (s.To.X - s.From.X) * u, s.From.Y + (s.To.Y - s.From.Y) * u);
        }
        return pos;
    }

    /// <summary>時刻 t で表示すべき実況（最後に閾値を跨いだ caption）。mock: draw() の caption ループ。</summary>
    public static string CaptionAt(PlaybackPlay play, double t)
    {
        var text = "";
        foreach (var c in play.Caps)
            if (t >= c.T) text = c.Text;
        return text;
    }
}

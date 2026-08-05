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

    /// <summary>
    /// 送球の投げ手（Issue E1。<see cref="BallSegment.FromRole"/> の写し。無ければ null）。
    /// 3D守備ビューが「誰が投げるモーションをするか」を決めるのに使う表示専用の値で、
    /// 2D俯瞰は読まない。モックの <see cref="ThrowSegment"/> は持たない＝Unity 側は
    /// null のとき従来どおり最近傍の野手へ落とす。
    /// </summary>
    public FieldPosition? FromRole { get; init; }

    /// <summary>送球の受け手（同上）。中継のカットマンもここに入る。</summary>
    public FieldPosition? ToRole { get; init; }

    /// <summary>受け先の塁名（"first"/"second"/"third"/"home"）。塁でなければ null。</summary>
    public string? ToBase { get; init; }
}

/// <summary>投球（マウンド→本塁）。mock: pitchSeg。</summary>
public sealed record PitchSegment(double StartT) : PlaybackBallSegment(StartT, StartT + DurationSeconds)
{
    /// <summary>投球1球の所要時間[秒]（mock: pitchSeg の 0.45 と同値）。</summary>
    public const double DurationSeconds = 0.45;

    public override PlaybackBall At(double tt)
    {
        var u = tt / DurationSeconds;
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
public sealed record PlaybackRun(double T0, double T1, PlaybackVec From, PlaybackVec To)
{
    /// <summary>このレッグの終端でアウトになったか（判定オーバーレイ, Issue #59）。</summary>
    public bool OutAtEnd { get; init; }
    /// <summary>このレッグの終端が守備との際どい判定だったか（セーフ側のみ表示に使う, Issue #59）。</summary>
    public bool CloseCall { get; init; }
    /// <summary>
    /// 表示専用（issue #489）: <see cref="OutAtEnd"/> の到達がフォース（塁を踏むだけ）かタッチプレー
    /// （野手が走者へタッチしにいく）かの区別。<see cref="RunnerLeg.Force"/> をそのまま運ぶ。
    /// </summary>
    public bool Force { get; init; }
}

/// <summary>走者（複数レッグ＋任意の消滅時刻 hideAt＋表示ラベル）。mock: runners[i]。</summary>
public sealed record PlaybackRunner
{
    public string Label { get; init; } = "打";
    public required IReadOnlyList<PlaybackRun> Segs { get; init; }
    public double? HideAt { get; init; }
    /// <summary>この走者の本人（対決ビューの頭上フルネームラベル用, issue #393）。不明なら null。</summary>
    public DuelPlayerIdentity? Identity { get; init; }
}

/// <summary>時刻付き実況キャプション。mock: caps[i] = [t, text]。</summary>
public readonly record struct PlaybackCaption(double T, string Text);

/// <summary>
/// 判定イベント1件（再生用, Issue #260）。<see cref="KokoSim.Engine.Match.Timeline.JudgementEvent"/>
/// の再生側ミラー。走者レッグとは独立した確定時刻を持つ。
/// </summary>
public readonly record struct PlaybackJudgement(double T, JudgementKind Kind, bool CloseCall, string Label,
    double? DistanceM = null);

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
    /// <summary>
    /// 野手の出発点（<see cref="KokoSim.Engine.Match.Timeline.PlayTimeline.FielderStarts"/> の写し）。
    /// null＝モック（<see cref="PlaybackSamples"/>）なので <see cref="PlaybackEvaluator.DefaultPositions"/> に落ちる。
    /// </summary>
    public IReadOnlyDictionary<FieldPosition, PlaybackVec>? Starts { get; init; }
    public IReadOnlyList<PlaybackRunner> Runners { get; init; } = Array.Empty<PlaybackRunner>();
    public IReadOnlyList<PlaybackCaption> Caps { get; init; } = Array.Empty<PlaybackCaption>();
    /// <summary>判定イベント列（Issue #260）。<see cref="PlaybackEvaluator.JudgementAt"/> の単一ソース。</summary>
    public IReadOnlyList<PlaybackJudgement> Judgements { get; init; } = Array.Empty<PlaybackJudgement>();
}

/// <summary>
/// 再生評価器（純関数群）。mock: ballAt / fielderAt / runnerAt / easeIO の忠実移植。
/// 時刻 t を与えると各アクターの位置を返す（描画はこの出力を写すだけ）。
/// </summary>
public static class PlaybackEvaluator
{
    /// <summary>
    /// 野手9人の初期守備位置[m]（mock DEF の写経）。
    /// ⚠ **実試合の再生でこれを使わない**。engine の守備解決は <see cref="FieldGeometry"/> の守備位置から
    /// 距離と時間を出しており、この表とは最大10m（中堅 92 対 82）ずれている＝出発点にすると
    /// レッグの長さが嘘になる。実出力は <see cref="PlaybackPlay.Starts"/> を持つのでそちらが使われる。
    /// ここが効くのは <see cref="PlaybackSamples"/>（モック）だけ。
    /// </summary>
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

    /// <summary>
    /// 野手移動の加減速ランプが区間長に占める割合（台形速度プロファイルの肩）。
    /// ピーク速度は平均の 1/(1−a) 倍＝<c>a=0.15</c> で 1.176 倍。
    /// </summary>
    public const double MoveRampFraction = 0.15;

    /// <summary>
    /// 野手移動の進み方（台形速度プロファイル。両端 <see cref="MoveRampFraction"/> で加速/減速し、
    /// 中間は等速）。<see cref="EaseIO"/>（smoothstep）は**ピーク速度が平均の1.5倍**になり、
    /// レッグの平均が既にスプリント速度（engine が距離÷脚力で尺を決めている）なので、
    /// 完璧なレッグでもピークが 10.88 m/s＝39km/h になっていた。端点の時刻・位置は不変
    /// ＝判定とは無関係で、中間の進み方だけを実速へ寄せる。
    /// </summary>
    public static double MoveEase(double u)
    {
        if (u <= 0) return 0;
        if (u >= 1) return 1;
        const double a = MoveRampFraction;
        var v = 1.0 / (1.0 - a);                       // 巡航速度（正規化）
        if (u < a) return v * u * u / (2 * a);         // 加速
        if (u <= 1 - a) return v * (a / 2 + (u - a));  // 等速
        var w = 1 - u;
        return 1 - v * w * w / (2 * a);                // 減速
    }

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
        // 出発点はこのプレーの守備配置（＝判定が距離と時間を出した座標）。持っていないのはモックだけ。
        var pos = play.Starts is { } starts && starts.TryGetValue(key, out var s) ? s : DefaultPositions[key];
        var moving = false;
        if (play.Moves != null && play.Moves.TryGetValue(key, out var segs))
        {
            foreach (var m in segs)
            {
                if (t <= m.T0) break;
                var u = MoveEase((t - m.T0) / (m.T1 - m.T0));
                pos = new PlaybackVec(pos.X + (m.To.X - pos.X) * u, pos.Y + (m.To.Y - pos.Y) * u);
                if (t < m.T1) moving = true;
            }
        }
        return (pos, moving);
    }

    /// <summary>時刻 t の走者位置（hideAt 経過後は null＝非表示）。mock: runnerAt。</summary>
    /// <param name="respectHideAt">
    /// <see cref="PlaybackRunner.HideAt"/> を効かせるか（既定 true＝従来どおり）。
    ///
    /// <c>HideAt</c> は**2D俯瞰の都合**で、アウトになった走者の丸トークンを盤面から消すためにある。
    /// 3Dの守備ビューでそのまま使うと**選手が空中で消える**（Issue F7）ので、3D側は false を渡して
    /// 位置だけを引き、アウトの表現は演技（スライディング失敗・タッチアウト）で見せる。
    /// 補間そのものはここが単一ソースのまま＝2Dと3Dで走者の居場所がずれない。
    /// </param>
    public static PlaybackVec? RunnerAt(PlaybackRunner r, double t, bool respectHideAt = true)
    {
        if (respectHideAt && r.HideAt.HasValue && t > r.HideAt.Value) return null;
        PlaybackVec? pos = null;
        foreach (var s in r.Segs)
        {
            if (t < s.T0) return pos;
            var u = Math.Min(1, (t - s.T0) / (s.T1 - s.T0));
            pos = new PlaybackVec(s.From.X + (s.To.X - s.From.X) * u, s.From.Y + (s.To.Y - s.From.Y) * u);
        }
        return pos;
    }

    /// <summary>
    /// 時刻 t で表示すべき判定（Issue #59 の判定オーバーレイ、Issue #328②で「フェア」を追加）。
    /// 確定時刻 T が <c>[t - holdSeconds, t]</c> に入る判定のうち直近（最大 T）を返す。判定が無ければ null。
    /// 判定イベント列（<see cref="PlaybackPlay.Judgements"/>）が単一ソース（Issue #260。
    /// 走者レッグの終端に紐づけない＝フライの捕球アウトは捕球の瞬間に出せる）。
    /// アウトは常時、セーフは <see cref="PlaybackJudgement.CloseCall"/>（際どい判定）だけを対象にする
    /// ＝出し分けは既にデータ側でゲート済み。描画専用の純関数で、判定/帯には一切関与しない。
    /// </summary>
    /// <summary>
    /// エラー判定の**表示だけ**を物理時刻（球に触れた/こぼした瞬間）から遅らせる演出定数[s]
    /// （2026-08-05 監督指示「触れたタイミングですぐ出るのをワンテンポ遅らせたい」）。
    /// 実際の記録員のE点灯と同じ「一拍おいて確定」の間。判定イベントの T（物理時刻）は動かさない
    /// ＝判定・帯・タイムラインの意味は不変で、表示窓の起点だけがずれる。
    /// 3D側のびっくりマーク・カメラ据え（DuelStageRig.PlanErrorMark）もこの定数を参照する（単一ソース）。
    /// </summary>
    public const double ErrorDisplayDelaySeconds = 0.5;

    /// <summary>エラーだけ表示起点を遅らせた「表示上の確定時刻」。</summary>
    private static double DisplayTimeOf(in PlaybackJudgement j)
        => j.Kind == JudgementKind.Error ? j.T + ErrorDisplayDelaySeconds : j.T;

    public static (JudgementKind Kind, double T1, double? DistanceM)? JudgementAt(
        PlaybackPlay play, double t, double holdSeconds)
    {
        if (play is null) return null;
        (JudgementKind Kind, double T1, double? DistanceM)? best = null;
        foreach (var j in play.Judgements)
        {
            var dt = DisplayTimeOf(j);
            if (dt > t || dt <= t - holdSeconds) continue;   // 表示窓 [T, T+hold) の外
            if (best.HasValue && dt < best.Value.T1) continue; // 併殺等は直近の判定だけ出す
            best = (j.Kind, dt, j.DistanceM);
        }
        return best;
    }

    /// <summary>
    /// プレー内で最後に出る判定の時刻（判定が無ければ null）。表示窓の終端を求めるのに使う
    /// ＝再生の早切り時刻がこれ＋hold を下回ると判定が一度も見えない（issue #236 症状A）。
    /// </summary>
    public static double? LastJudgementTime(PlaybackPlay play)
    {
        if (play is null) return null;
        double? last = null;
        foreach (var j in play.Judgements)
        {
            // エラーは表示起点が遅れる（ErrorDisplayDelaySeconds）ぶん窓の終端も遅い＝
            // 表示上の時刻で比べないと、遅延したテロップが早切りに刈られる（issue #236 症状Aと同型）
            var dt = DisplayTimeOf(j);
            if (!last.HasValue || dt > last.Value) last = dt;
        }
        return last;
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

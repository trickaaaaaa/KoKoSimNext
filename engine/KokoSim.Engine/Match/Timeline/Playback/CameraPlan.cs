using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline.Playback;

// ─────────────────────────────────────────────────────────────────────────────
// タイムライン駆動のカメラワーク（design-06/design-12・Issue #119）。
// PlaybackPlay から「再生開始前に」カメラキーフレーム列を生成する純関数（UnityEngine非依存）。
// リアルタイム追跡ではない＝プレーが確定済みであることを前提に、未来のボール位置も先読みして使う。
//
// Viewport は field-space の中心 (Cx,Cy)[m] とズーム倍率 ZoomMult の3値。ZoomMult=1 は
// 「全景（Match2DPlaybackElement の既存固定ビュー＝デザインキャンバス DesignW=760/DesignH=520/S=3.35
// が丸ごと収まる状態）」と定義する（3定数はUnity側と同期。変更時は両方合わせる）。
// Unity側は既存の Scale(k) にこの ZoomMult を掛け、中心を (Cx,Cy) に取り直すだけで描画へ反映できる
// （線の太さ・トークン半径は k 由来のスケールのまま＝画面上で一定、座標だけがズームで動く）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>カメラ視野（field-space中心[m]・ズーム倍率）。</summary>
public readonly record struct CameraViewport(double Cx, double Cy, double ZoomMult);

/// <summary>
/// フレーミングの詰め方（ビューごとに変える3つ）。演出の判断（何を追うか・いつ全景へ戻すか）や
/// レート制限（酔い防止）は共有し、**どれだけ寄るか**だけを差し替えるための束。
///
/// 3Dの守備カメラは2Dの俯瞰より深く寄る必要がある: <see cref="CameraFraming.Default"/> の
/// <c>MaxZoom=3.4</c> は縦45.6mに相当し、身長1.75mの野手が480×270のRTで十数pxにしかならない。
/// 外野フライで <c>FitBox</c> が求めるズームは実測5.75で、**上限に頭打ちされている**。
/// </summary>
/// <param name="MaxZoom">ズーム倍率の上限（1=全景）。大きいほど深く寄る。</param>
/// <param name="MarginM">
/// 被写体まわりに残す余白[m]。**寄りの深さを実際に決めているのはこちら**で、<paramref name="MaxZoom"/> ではない。
/// <c>FitBox</c> は箱に 2×Margin を足してからズームを決めるので、箱が点まで縮んでも
/// frameH は 2×Margin より小さくならない（既定 6.0m なら上限 12.9倍で頭打ち）。
/// </param>
/// <param name="PitchZoom">投球区間の固定フレームのズーム倍率。</param>
/// <param name="ThrowFollowsBall">
/// 送球区間の枠の取り方。<c>false</c>（既定・2D俯瞰）＝**始点と終点を両方収める**。
/// 小さな盤面で送球の向きを読ませる作法だが、寄りたい3Dでは「6-3 なら遊撃〜一塁の30mを必ず枠に入れる」
/// ＝どれだけ Margin を詰めても寄れない、という形で効く（実測: 決着の瞬間が frameH 20〜56m ＝
/// そのプレーで最も引いた絵になる）。<c>true</c> なら打球中と同じく**球を先読みで追う**＝
/// 送球が届くにつれ枠が締まり、受け手の捕球で最も寄る（中継カメラの作法）。
/// </param>
public readonly record struct CameraFraming(
    double MaxZoom, double MarginM, double PitchZoom, bool ThrowFollowsBall = false)
{
    /// <summary>2D俯瞰の既定（Issue #119 で決めた現行値）。渡さなければこれが使われる。</summary>
    public static readonly CameraFraming Default = new(3.4, 6.0, 2.4);
}

/// <summary>カメラプランのキーフレーム1点。区間は easeIO で補間する（<see cref="PlaybackEvaluator.EaseIO"/> と同じ形）。</summary>
public readonly record struct CameraKeyframe(double T, double Cx, double Cy, double ZoomMult);

/// <summary>
/// PlaybackPlay からカメラキーフレーム列を生成する（design-06/design-12・Issue #119）。
/// 一定間隔でサンプリングした「あるべき視野」を、ズーム変化率・パン速度の上限でレート制限しながら
/// 追従させて作る（酔い防止）。事前計画型ゆえ、未来の必要点を少し先取り（<see cref="LeadSeconds"/>）
/// して追従させることで、上限があっても実際の被写体からの遅れを抑える。
/// </summary>
public static class CameraPlanBuilder
{
    // 全景の基準（Match2DPlaybackElement.cs の DesignW=760/DesignH=520/S=3.35 と同期）。
    private const double DesignWidthM = 760.0 / 3.35;
    private const double DesignHeightM = 520.0 / 3.35;
    public const double WideCx = 0.0;
    public const double WideCy = (492.0 - 520.0 / 2.0) / 3.35; // ≈69.25m（本塁〜デザインキャンバス中心の縦オフセット）
    public const double WideZoom = 1.0;

    // 数値は完了条件（酔わない・被写体を捉え続ける）を満たすための実装判断値。要調整ならここを直す。
    // MaxZoom / MarginM / PitchZoom は CameraFraming でビューごとに差し替えられる（既定は下の値）。
    public const double LookaheadSeconds = 0.6;         // 「現在のボール位置＋0.6秒先」
    private const double SampleStepSeconds = 0.08;      // キーフレーム間隔（十分密＝easeIO区間はほぼ線形）
    private const double LeadSeconds = 0.25;            // 先取りサンプリング（事前計画ゆえの遅れ対策）
    // ResAt から全景へ戻すまでの間。3Dの守備ビューは全景へ戻さず**この時刻の枠のまま止める**
    // （2Dは全景に全部入るので戻して問題ないが、寄った3Dでは「用のないところを見ている1.5秒」になる）
    // ので、戻し始める時刻を単一ソースとして公開する。
    public const double ReturnToWideDelaySeconds = 0.9;
    private const double WideSettleBufferSeconds = 3.0;  // 全景へのレート制限付き復帰が収束するまでの生成余白（実再生は play.Dur までしか参照しないため無害）
    private const double MaxPlanSeconds = 120.0;         // キーフレーム生成の上限（#208 暴走・OOM 防止。1プレーは現実に数秒）

    public const double ZoomRateCapPerSecond = 2.0;  // ズーム変化は毎秒2倍以内
    public const double PanSpeedCapMPerSecond = 45.0; // パン速度上限（実測球速に追従しつつ急旋回を避ける値）

    /// <param name="framing">
    /// 寄りの深さ。省略＝<see cref="CameraFraming.Default"/>＝2D俯瞰の現行値で、
    /// **渡さなければ出力は1キーフレームも変わらない**（既存の呼び出しとゴールデンは無傷）。
    /// 3Dの守備カメラは <c>MaxZoom</c> を上げた別セットを渡す。
    /// </param>
    public static IReadOnlyList<CameraKeyframe> Build(PlaybackPlay play, CameraFraming? framing = null)
    {
        var fr = framing ?? CameraFraming.Default;
        // ResAt は「結果チップを出さない」プレー（投球のみ＝PitchPlaybackFactory）で +∞ になる。そのまま
        // end に混ぜると end=∞ となり、下のサンプリングループ（clampedT>=end で停止）が永久に止まらず
        // キーフレームを確保し続けて OOM する（#208 の真因）。非有限な ResAt は「無し」として無視する。
        var resEnd = double.IsFinite(play.ResAt) ? play.ResAt + ReturnToWideDelaySeconds : 0.0;
        var dur = double.IsFinite(play.Dur) ? play.Dur : 0.0;
        // 1プレーの再生は現実的に数秒。異常に長い（or 非有限）継続時間でもキーフレーム暴走で OOM しないよう上限を設ける。
        var end = Math.Min(Math.Max(dur, resEnd) + WideSettleBufferSeconds, MaxPlanSeconds);
        var kfs = new List<CameraKeyframe>();

        double cx = WideCx, cy = WideCy, zoom = WideZoom;
        var prevT = 0.0;
        var first = true;
        foreach (var clampedT in SampleTimes(play, end))
        {
            var raw = RawTargetAt(play, clampedT, Math.Min(clampedT + LeadSeconds, end), fr);
            if (first)
            {
                cx = raw.Cx; cy = raw.Cy; zoom = raw.ZoomMult;
                first = false;
            }
            else
            {
                // 実際に経過した時間（境界サンプルは間隔が SampleStepSeconds より短くなり得る）でレート制限する。
                var dt = clampedT - prevT;
                zoom = ClampZoomRate(zoom, raw.ZoomMult, dt);
                (cx, cy) = ClampPan(cx, cy, raw.Cx, raw.Cy, dt);
            }
            kfs.Add(new CameraKeyframe(clampedT, cx, cy, zoom));
            prevT = clampedT;
        }
        return kfs;
    }

    /// <summary>
    /// キーフレームを打つ時刻の列（昇順・重複なし）。一定間隔のサンプリングに加えて、
    /// ボールセグメントの境界時刻（投球→打球等の切り替わり）も必ず打つ。等間隔サンプリングだけだと
    /// 区間の変わり目が2キーフレームの間（easeIO補間区間）に埋もれ、実際の切り替わりより早く
    /// 視野が動き出してしまう（Issue #258: 投球中に打つ前からカメラが動く症状の残存要因）。
    /// </summary>
    private static IEnumerable<double> SampleTimes(PlaybackPlay play, double end)
    {
        var times = new SortedSet<double>();
        for (var t = 0.0; t < end; t += SampleStepSeconds)
            times.Add(t);
        times.Add(end);

        foreach (var seg in play.Ball)
        {
            if (seg.T0 > 0 && seg.T0 < end) times.Add(seg.T0);
            if (seg.T1 > 0 && seg.T1 < end) times.Add(seg.T1);
        }
        return times;
    }

    private static double ClampZoomRate(double prev, double target, double dt)
    {
        var maxRatio = Math.Pow(ZoomRateCapPerSecond, dt);
        var lo = prev / maxRatio;
        var hi = prev * maxRatio;
        return Math.Clamp(target, lo, hi);
    }

    private static (double Cx, double Cy) ClampPan(double px, double py, double tx, double ty, double dt)
    {
        var maxDist = PanSpeedCapMPerSecond * dt;
        var dx = tx - px;
        var dy = ty - py;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist <= maxDist || dist < 1e-9) return (tx, ty);
        var k = maxDist / dist;
        return (px + dx * k, py + dy * k);
    }

    /// <summary>
    /// 時刻 t で「本来映すべき」視野（レート制限をかける前の生の目標値）。
    /// <paramref name="actualT"/>=実際の経過時刻（区間判定用）、<paramref name="leadT"/>=先取りサンプリング時刻
    /// （<see cref="LeadSeconds"/> 分先。ボール追従の目標算出だけに使う）。区間判定を leadT でやると、
    /// 投球区間の終盤（接触の LeadSeconds 秒前）で先取りが接触後の区間へ食い込み、打つ前からパンが
    /// 始まってしまう（Issue #258）。固定フレーム区間中かどうかは常に actualT で判定する。
    /// </summary>
    private static CameraViewport RawTargetAt(PlaybackPlay play, double actualT, double leadT, CameraFraming fr)
    {
        if (leadT >= play.ResAt + ReturnToWideDelaySeconds) return Wide();

        var (covering, nextUpcoming) = FindSegmentAt(play, actualT);

        // 投球区間は固定フレーム。ビュー側の忠実移植モデル（PitchSegment）とエンジン実出力
        // （EndpointBallSegment＋Kind=Pitch。#258）の両方を拾う。
        if (IsKind(covering, BallSegmentKind.Pitch)
            || (covering is null && IsKind(nextUpcoming, BallSegmentKind.Pitch)))
            return FitBox(new PlaybackVec(0, 0), new PlaybackVec(FieldDiagramGeometry.Mound.X, FieldDiagramGeometry.Mound.Y), fr, fr.PitchZoom);

        // 送球区間は始点・終点を収めるフレーム。同じく両モデルを拾う（#258）。
        // ThrowFollowsBall なら下の「ボールを追う」へ落とす＝送球も打球と同じ扱いになる。
        if (!fr.ThrowFollowsBall && IsKind(covering, BallSegmentKind.Throw))
        {
            var from = covering!.At(0);
            var to = covering.At(covering.T1 - covering.T0);
            return FitBox(new PlaybackVec(from.X, from.Y), new PlaybackVec(to.X, to.Y), fr);
        }

        // 打球（滞空/転がり）中、またはボール保持中の狭間 ＝ ボールを追う。
        // ただし走塁の結末に絡む区間（盗塁・走塁死・本塁クロスプレー）は「ボールと目標塁の中点」を追う。
        var critical = FindCriticalRunAt(play, leadT);
        if (critical != null)
        {
            var ballNow = PlaybackEvaluator.BallAt(play, leadT);
            var ballPos = ballNow.HasValue ? new PlaybackVec(ballNow.Value.X, ballNow.Value.Y) : critical.To;
            return FitBox(ballPos, critical.To, fr);
        }

        var cur = PlaybackEvaluator.BallAt(play, leadT);
        if (!cur.HasValue) return Wide();
        var curV = new PlaybackVec(cur.Value.X, cur.Value.Y);
        var ahead = PlaybackEvaluator.BallAt(play, leadT + LookaheadSeconds);
        var aheadV = ahead.HasValue ? new PlaybackVec(ahead.Value.X, ahead.Value.Y) : curV;
        return FitBox(curV, aheadV, fr);
    }

    /// <summary>
    /// セグメントが指定種別か（ビュー側の忠実移植モデル＝固定4型と、エンジン実出力の
    /// <see cref="EndpointBallSegment"/>＋<see cref="BallSegmentKind"/> の両方を同一視して判定する。#258）。
    /// </summary>
    private static bool IsKind(PlaybackBallSegment? seg, BallSegmentKind kind) => (seg, kind) switch
    {
        (PitchSegment, BallSegmentKind.Pitch) => true,
        (ThrowSegment, BallSegmentKind.Throw) => true,
        (EndpointBallSegment eb, _) => eb.Kind == kind,
        _ => false,
    };

    private static (PlaybackBallSegment? Covering, PlaybackBallSegment? NextUpcoming) FindSegmentAt(PlaybackPlay play, double t)
    {
        foreach (var s in play.Ball)
        {
            if (t < s.T0) return (null, s);
            if (t <= s.T1) return (s, null);
        }
        return (null, null);
    }

    private static PlaybackRun? FindCriticalRunAt(PlaybackPlay play, double t)
    {
        PlaybackRun? found = null;
        foreach (var runner in play.Runners)
            foreach (var seg in runner.Segs)
                if ((seg.OutAtEnd || seg.CloseCall) && t >= seg.T0 && t <= seg.T1)
                    found = seg;
        return found;
    }

    private static CameraViewport Wide() => new(WideCx, WideCy, WideZoom);

    private static CameraViewport FitBox(PlaybackVec a, PlaybackVec b, CameraFraming fr, double? fixedZoom = null)
    {
        var cx = (a.X + b.X) / 2.0;
        var cy = (a.Y + b.Y) / 2.0;
        if (fixedZoom.HasValue) return new CameraViewport(cx, cy, Math.Clamp(fixedZoom.Value, WideZoom, fr.MaxZoom));

        var w = Math.Abs(a.X - b.X) + fr.MarginM * 2.0;
        var h = Math.Abs(a.Y - b.Y) + fr.MarginM * 2.0;
        var zoom = Math.Min(DesignWidthM / w, DesignHeightM / h);
        return new CameraViewport(cx, cy, Math.Clamp(zoom, WideZoom, fr.MaxZoom));
    }
}

/// <summary>キーフレーム列から時刻 t の視野を求める（easeIO 補間。区間外はクランプ）。</summary>
public static class CameraEvaluator
{
    public static CameraViewport ViewportAt(IReadOnlyList<CameraKeyframe> plan, double t)
    {
        if (plan == null || plan.Count == 0) return new CameraViewport(CameraPlanBuilder.WideCx, CameraPlanBuilder.WideCy, CameraPlanBuilder.WideZoom);
        if (t <= plan[0].T) return AsViewport(plan[0]);
        if (t >= plan[^1].T) return AsViewport(plan[^1]);

        for (var i = 1; i < plan.Count; i++)
        {
            if (t > plan[i].T) continue;
            var prev = plan[i - 1];
            var next = plan[i];
            var span = next.T - prev.T;
            var u = span > 0 ? PlaybackEvaluator.EaseIO((t - prev.T) / span) : 1.0;
            var cx = prev.Cx + (next.Cx - prev.Cx) * u;
            var cy = prev.Cy + (next.Cy - prev.Cy) * u;
            // ズームは対数空間で補間（倍率変化を等速に見せる。区間が短いので実質線形に近い）。
            var logZoom = Math.Log(prev.ZoomMult) + (Math.Log(next.ZoomMult) - Math.Log(prev.ZoomMult)) * u;
            return new CameraViewport(cx, cy, Math.Exp(logZoom));
        }
        return AsViewport(plan[^1]);
    }

    private static CameraViewport AsViewport(CameraKeyframe k) => new(k.Cx, k.Cy, k.ZoomMult);
}

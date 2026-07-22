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
    private const double MaxZoom = 3.4;
    private const double MarginM = 6.0;                 // 被写体まわりの余白
    private const double PitchZoom = 2.4;               // 投手ー打者フレーミング（「内野の縦半分程度」の近似値）
    public const double LookaheadSeconds = 0.6;         // 「現在のボール位置＋0.6秒先」
    private const double SampleStepSeconds = 0.08;      // キーフレーム間隔（十分密＝easeIO区間はほぼ線形）
    private const double LeadSeconds = 0.25;            // 先取りサンプリング（事前計画ゆえの遅れ対策）
    private const double ReturnToWideDelaySeconds = 0.9; // ResAt から全景へ戻すまでの間
    private const double WideSettleBufferSeconds = 3.0;  // 全景へのレート制限付き復帰が収束するまでの生成余白（実再生は play.Dur までしか参照しないため無害）

    public const double ZoomRateCapPerSecond = 2.0;  // ズーム変化は毎秒2倍以内
    public const double PanSpeedCapMPerSecond = 45.0; // パン速度上限（実測球速に追従しつつ急旋回を避ける値）

    public static IReadOnlyList<CameraKeyframe> Build(PlaybackPlay play)
    {
        var end = Math.Max(play.Dur, play.ResAt + ReturnToWideDelaySeconds) + WideSettleBufferSeconds;
        var kfs = new List<CameraKeyframe>();

        double cx = WideCx, cy = WideCy, zoom = WideZoom;
        var prevT = 0.0;
        var first = true;
        for (var t = 0.0; ; t += SampleStepSeconds)
        {
            var clampedT = Math.Min(t, end);
            var raw = RawTargetAt(play, Math.Min(clampedT + LeadSeconds, end));
            if (first)
            {
                cx = raw.Cx; cy = raw.Cy; zoom = raw.ZoomMult;
                first = false;
            }
            else
            {
                // 実際に経過した時間（最終サンプルは end で切り詰められ SampleStepSeconds より短くなり得る）でレート制限する。
                var dt = clampedT - prevT;
                zoom = ClampZoomRate(zoom, raw.ZoomMult, dt);
                (cx, cy) = ClampPan(cx, cy, raw.Cx, raw.Cy, dt);
            }
            kfs.Add(new CameraKeyframe(clampedT, cx, cy, zoom));
            prevT = clampedT;
            if (clampedT >= end) break;
        }
        return kfs;
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

    /// <summary>時刻 t で「本来映すべき」視野（レート制限をかける前の生の目標値）。</summary>
    private static CameraViewport RawTargetAt(PlaybackPlay play, double t)
    {
        if (t >= play.ResAt + ReturnToWideDelaySeconds) return Wide();

        var (covering, nextUpcoming) = FindSegmentAt(play, t);

        if (covering is PitchSegment || (covering is null && nextUpcoming is PitchSegment))
            return FitBox(new PlaybackVec(0, 0), new PlaybackVec(FieldDiagramGeometry.Mound.X, FieldDiagramGeometry.Mound.Y), PitchZoom);

        if (covering is ThrowSegment th)
        {
            var from = th.At(0);
            var to = th.At(th.T1 - th.T0);
            return FitBox(new PlaybackVec(from.X, from.Y), new PlaybackVec(to.X, to.Y));
        }

        // 打球（滞空/転がり）中、またはボール保持中の狭間 ＝ ボールを追う。
        // ただし走塁の結末に絡む区間（盗塁・走塁死・本塁クロスプレー）は「ボールと目標塁の中点」を追う。
        var critical = FindCriticalRunAt(play, t);
        if (critical != null)
        {
            var ballNow = PlaybackEvaluator.BallAt(play, t);
            var ballPos = ballNow.HasValue ? new PlaybackVec(ballNow.Value.X, ballNow.Value.Y) : critical.To;
            return FitBox(ballPos, critical.To);
        }

        var cur = PlaybackEvaluator.BallAt(play, t);
        if (!cur.HasValue) return Wide();
        var curV = new PlaybackVec(cur.Value.X, cur.Value.Y);
        var ahead = PlaybackEvaluator.BallAt(play, t + LookaheadSeconds);
        var aheadV = ahead.HasValue ? new PlaybackVec(ahead.Value.X, ahead.Value.Y) : curV;
        return FitBox(curV, aheadV);
    }

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

    private static CameraViewport FitBox(PlaybackVec a, PlaybackVec b, double? fixedZoom = null)
    {
        var cx = (a.X + b.X) / 2.0;
        var cy = (a.Y + b.Y) / 2.0;
        if (fixedZoom.HasValue) return new CameraViewport(cx, cy, Math.Clamp(fixedZoom.Value, WideZoom, MaxZoom));

        var w = Math.Abs(a.X - b.X) + MarginM * 2.0;
        var h = Math.Abs(a.Y - b.Y) + MarginM * 2.0;
        var zoom = Math.Min(DesignWidthM / w, DesignHeightM / h);
        return new CameraViewport(cx, cy, Math.Clamp(zoom, WideZoom, MaxZoom));
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

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Unity.Match;
using UnityEditor;
using UnityEngine;

namespace KokoSim.Unity.EditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】暴投・捕逸の守備カメラの実測（issue #566）。使い捨て。
    ///
    /// <para>「カメラが空の地面を向いて誰も映らない」は絵では**狙いを外しているのか寄りすぎなのか
    /// 区別できない**（design-18 §3.3 の申し送りと同型）。ここでは engine のカメラプランを
    /// そのまま組み、実カメラ（<c>WorldToViewportPoint</c>）で被写体が枠に入っているかを
    /// 0.05秒刻みで数える＝どの区間で誰が外れているかを数字で出す。</para>
    ///
    /// <para>Play に入らない（Edit mode）。リグも建てない＝<see cref="DuelStageRig"/> の
    /// 据え位置・枠の定数だけを読んで同じ計算を再現する。</para>
    /// </summary>
    public static class BatteryMissCameraProbe
    {
        // 3D守備ビューのRT（DuelStageRig の既定と同じ縦横比）。枠の横幅は縦×アスペクトで決まる。
        private const int RtW = 960;
        private const int RtH = 540;

        [MenuItem("KokoSim/Probe/暴投・捕逸のカメラを実測（issue #566）")]
        public static void Run()
        {
            var field = new FieldGeometry();
            var c = new KokoSim.Engine.Match.Game.BaserunningCoefficients();

            // 走者は一・三塁（design-14 P2-8 の必須要件＝三塁走者は生還する）。
            var first = Guy("走一");
            var third = Guy("走三");
            var catcher = Guy("捕手");

            var tl = TimelineBuilder.BuildBatteryMiss(first, null, third, catcher, c, field);
            var play = PlayTimelineAdapter.ToPlaybackPlay(tl);

            var sb = new StringBuilder();

            // 直す前（球を追うだけ）と直した後（拾いに走る野手を入れる）を並べる。
            Measure(play, false, sb, detail: true);
            Measure(play, true, sb, detail: true);

            Debug.Log(sb.ToString());
            System.IO.Directory.CreateDirectory("out");
            System.IO.File.WriteAllText("out/battery-miss-camera-566.txt", sb.ToString());
        }

        private static void Measure(PlaybackPlay play, bool chaser, StringBuilder sb, bool detail)
        {
            // 直した後は「投げ返しは始点・終点を両方収める」も一緒に効く（DuelStageRig と同じ判定）。
            var framing = new CameraFraming(
                DuelStageRig.FieldMaxZoom, DuelStageRig.FieldMarginM, CameraFraming.Default.PitchZoom,
                ThrowFollowsBall: !chaser && DuelStageRig.FieldThrowFollowsBall,
                HeldBallFollowsRunner: true, LooseBallFollowsChaser: chaser);
            var plan = CameraPlanBuilder.Build(play, framing, 0.0);

            // 実際に見せる長さ（issue #522 の打ち切り）。ここより後ろは再生されないので数えない。
            var wideStart = CameraPlanBuilder.WideStartTime(play);
            var shownEnd = double.IsFinite(wideStart) ? System.Math.Min(play.Dur, wideStart - 0.3) : play.Dur;

            var cam = NewProbeCamera();
            sb.Append("\n════ ")
              .Append(chaser ? "直した後（拾いに走る野手を被写体へ）" : "直す前（球だけ追う→走者へ飛ぶ）")
              .Append("  ／ 見せる範囲 t=0.00〜").Append(shownEnd.ToString("F2"))
              .Append("（Dur=").Append(play.Dur.ToString("F2")).Append("・#522で打ち切り）\n");
            if (detail)
            {
                foreach (var b in play.Ball)
                    sb.Append("  球 t=").Append(b.T0.ToString("F2")).Append("→").Append(b.T1.ToString("F2")).Append('\n');
                foreach (var r in play.Runners)
                    foreach (var s2 in r.Segs)
                        sb.Append("  走 ").Append(r.Label).Append(" t=").Append(s2.T0.ToString("F2"))
                          .Append("→").Append(s2.T1.ToString("F2")).Append('\n');
                sb.Append("  t   |  aim(x,y,z)          frameH | 捕手 走一 走三 球 | 画面/秒 | 枠内\n");
            }

            var blind = 0;
            var total = 0;
            double blindFrom = -1, blindTo = -1;
            var maxScreensPerSec = 0.0;
            Vector3 prevAim = Vector3.zero;
            var firstSample = true;
            for (var t = 0.0; t <= shownEnd + 1e-9; t += 0.05)
            {
                ComputeFrame(plan, play, (float)t, out var aim, out var frameH);
                AimCamera(cam, aim, frameH);
                var screensPerSec = 0.0;
                if (!firstSample)
                    screensPerSec = Vector2.Distance(new Vector2(prevAim.x, prevAim.z), new Vector2(aim.x, aim.z))
                                    / 0.05 / Mathf.Max(frameH, 0.01f);
                firstSample = false;
                prevAim = aim;
                if (screensPerSec > maxScreensPerSec) maxScreensPerSec = screensPerSec;

                var pCatcher = ActorAt(play, FieldPosition.Catcher, t);
                var pFirst = RunnerAt(play, "走一", t);
                var pThird = RunnerAt(play, "走三", t);
                var ball = PlaybackEvaluator.BallAt(play, t);

                var inCatcher = InFrame(cam, pCatcher, 0.9f);
                var inFirst = pFirst.HasValue && InFrame(cam, pFirst.Value, 0.9f);
                var inThird = pThird.HasValue && InFrame(cam, pThird.Value, 0.9f);
                var inBall = ball.HasValue
                    && InFrameWorld(cam, new Vector3((float)ball.Value.X, (float)ball.Value.H, (float)ball.Value.Y));

                // 「観戦者が何かを読める」＝**誰か1人でも**枠内（issue #566 の期待）。捕手・走者だけでなく
                // 送球の受け手（投手）も被写体なので、守備9人を全部見る。
                var readable = inFirst || inThird || AnyFielderInFrame(cam, play, t);
                total++;
                if (!readable)
                {
                    blind++;
                    if (blindFrom < 0) blindFrom = t;
                    blindTo = t;
                }

                if (detail)
                    sb.Append(t.ToString("F2").PadLeft(5)).Append(" | ")
                      .Append(Vec(aim)).Append("  ").Append(frameH.ToString("F1").PadLeft(5)).Append(" | ")
                      .Append(Mark(inCatcher)).Append("   ").Append(Mark(inFirst)).Append("   ")
                      .Append(Mark(inThird)).Append("   ").Append(Mark(inBall))
                      .Append(" | ").Append(screensPerSec.ToString("F1").PadLeft(6))
                      .Append("  | ").Append(readable ? "読める" : "★誰も居ない").Append('\n');
            }

            sb.Append("→ 誰も枠に居ないコマ: ").Append(blind).Append(" / ").Append(total)
              .Append("（").Append(((double)blind / total).ToString("P1")).Append("）");
            if (blind > 0)
                sb.Append("  区間 t=").Append(blindFrom.ToString("F2")).Append("〜")
                  .Append(blindTo.ToString("F2")).Append(" ＝ ")
                  .Append((blindTo - blindFrom + 0.05).ToString("F2")).Append("秒");
            sb.Append("  ／ パン最大 ").Append(maxScreensPerSec.ToString("F1")).Append(" 画面ぶん/秒\n");

            Object.DestroyImmediate(cam.gameObject);
        }

        /// <summary>
        /// 候補B: 「球＋進塁中の全走者」を毎コマ収める枠（＝転がっている間も引いたまま）。
        /// engine を変えずに枠だけ差し替えて、盲点コマ数と枠の広さを測る。
        /// </summary>
        private static void MeasureLooseBallWide(PlaybackPlay play, StringBuilder sb, bool withRunners)
        {
            var wideStart = CameraPlanBuilder.WideStartTime(play);
            var shownEnd = double.IsFinite(wideStart) ? System.Math.Min(play.Dur, wideStart - 0.3) : play.Dur;
            var cam = NewProbeCamera();
            sb.Append(withRunners ? "\n════ 候補B: 球＋進塁中の全走者を最初から収める  ／ 見せる範囲 t=0.00〜" : "\n════ 候補C: 球＋球を追う捕手だけを収める（走者は入れない）  ／ 見せる範囲 t=0.00〜")
              .Append(shownEnd.ToString("F2")).Append('\n');
            sb.Append("  t   |  aim(x,y,z)          frameH | 捕手 走一 走三 球 | 枠内\n");

            var blind = 0;
            var total = 0;
            var maxFrameH = 0f;
            for (var t = 0.0; t <= shownEnd + 1e-9; t += 0.05)
            {
                // 収めたい点＝球・捕手・進塁中の走者。FitBox と同じく余白 2×Margin を足す。
                var pts = new List<Vector2>();
                var ball = PlaybackEvaluator.BallAt(play, t);
                if (ball.HasValue) pts.Add(new Vector2((float)ball.Value.X, (float)ball.Value.Y));
                pts.Add(ActorAt(play, FieldPosition.Catcher, t));
                var pFirst = RunnerAt(play, "走一", t);
                var pThird = RunnerAt(play, "走三", t);
                if (withRunners)
                {
                    if (pFirst.HasValue) pts.Add(pFirst.Value);
                    if (pThird.HasValue) pts.Add(pThird.Value);
                }

                float minX = pts[0].x, maxX = pts[0].x, minY = pts[0].y, maxY = pts[0].y;
                foreach (var p in pts)
                {
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                }
                var m = DuelStageRig.FieldMarginM;
                var boxW = maxX - minX + 2f * m;
                var boxH = maxY - minY + 2f * m;
                // 横は縦×アスペクトで賄えるので、必要な縦＝max(boxH, boxW/aspect)。
                var frameH = Mathf.Max(boxH, boxW / (RtW / (float)RtH));
                maxFrameH = Mathf.Max(maxFrameH, frameH);
                var aim = new Vector3((minX + maxX) * 0.5f, 1.0f, (minY + maxY) * 0.5f);
                AimCamera(cam, aim, frameH);

                var inCatcher = InFrame(cam, ActorAt(play, FieldPosition.Catcher, t), 0.9f);
                var inFirst = pFirst.HasValue && InFrame(cam, pFirst.Value, 0.9f);
                var inThird = pThird.HasValue && InFrame(cam, pThird.Value, 0.9f);
                var inBall = ball.HasValue
                    && InFrameWorld(cam, new Vector3((float)ball.Value.X, (float)ball.Value.H, (float)ball.Value.Y));
                var readable = inFirst || inThird || AnyFielderInFrame(cam, play, t);
                total++;
                if (!readable) blind++;

                if (System.Math.Abs(t * 20 % 4) < 1e-6)   // 0.2秒おきに1行
                    sb.Append(t.ToString("F2").PadLeft(5)).Append(" | ")
                      .Append(Vec(aim)).Append("  ").Append(frameH.ToString("F1").PadLeft(5)).Append(" | ")
                      .Append(Mark(inCatcher)).Append("   ").Append(Mark(inFirst)).Append("   ")
                      .Append(Mark(inThird)).Append("   ").Append(Mark(inBall))
                      .Append(" | ").Append(readable ? "読める" : "★誰も居ない").Append('\n');
            }
            sb.Append("→ 誰も枠に居ないコマ: ").Append(blind).Append(" / ").Append(total)
              .Append("  ／ 枠の最大 ").Append(maxFrameH.ToString("F1")).Append("m（人の背丈は枠のおよそ ")
              .Append((1.75f / maxFrameH * RtH).ToString("F0")).Append("px）\n");
            Object.DestroyImmediate(cam.gameObject);
        }

        /// <summary>守備9人のうち1人でも枠内か（送球の受け手・カバーに入った野手も被写体）。</summary>
        private static bool AnyFielderInFrame(Camera cam, PlaybackPlay play, double t)
        {
            foreach (FieldPosition pos in System.Enum.GetValues(typeof(FieldPosition)))
            {
                if (pos == FieldPosition.DesignatedHitter) continue;
                if (InFrame(cam, ActorAt(play, pos, t), 0.9f)) return true;
            }
            return false;
        }

        // ── DuelStageRig.ComputeFieldFrame / FieldStationAt と同じ計算 ──

        private static void ComputeFrame(IReadOnlyList<CameraKeyframe> plan, PlaybackPlay play, float t,
            out Vector3 aim, out float frameH)
        {
            var vp = CameraEvaluator.ViewportAt(plan, t);
            var zoom = (float)System.Math.Max(vp.ZoomMult, 0.01);
            frameH = 520f / 3.35f / zoom;

            var aimY = 1.0f;   // FieldAimYMin
            var b0 = PlaybackEvaluator.BallAt(play, t);
            if (b0.HasValue)
            {
                var b1 = PlaybackEvaluator.BallAt(play, t + (float)CameraPlanBuilder.LookaheadSeconds);
                var h0 = (float)b0.Value.H;
                var h1 = b1.HasValue ? (float)b1.Value.H : h0;
                aimY = Mathf.Max(1.0f, (h0 + h1) * 0.5f);
                frameH = Mathf.Max(frameH, Mathf.Abs(h0 - h1) + DuelStageRig.FieldMarginM * 2f);
            }
            aim = new Vector3((float)vp.Cx, aimY, (float)vp.Cy);
        }

        /// <summary>据え位置は被写体の深さで内野/外野を切り替える（暴投は常に内野側になるはず）。</summary>
        private static Vector3 StationFor(Vector3 aim)
            => aim.z >= 45f ? DuelStageRig.FieldStationOutfield : DuelStageRig.FieldStationInfield;

        private static Camera NewProbeCamera()
        {
            var go = new GameObject("~BatteryMissCameraProbe") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.aspect = RtW / (float)RtH;
            return cam;
        }

        private static void AimCamera(Camera cam, Vector3 aim, float frameH)
        {
            var station = StationFor(aim);
            cam.transform.position = station;
            cam.transform.LookAt(aim);
            var dist = Vector3.Distance(station, aim);
            cam.fieldOfView = 2f * Mathf.Atan(frameH * 0.5f / Mathf.Max(dist, 0.01f)) * Mathf.Rad2Deg;
        }

        /// <summary>地面の点（x,z）が枠内か。<paramref name="standHeight"/>＝人の腰の高さ[m]で見る。</summary>
        private static bool InFrame(Camera cam, Vector2 p, float standHeight)
            => InFrameWorld(cam, new Vector3(p.x, standHeight, p.y));

        private static bool InFrameWorld(Camera cam, Vector3 world)
        {
            var v = cam.WorldToViewportPoint(world);
            return v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
        }

        // ── 被写体の位置 ──

        // 位置は再生評価器（engine の純関数）から引く＝描画と同じ位置を見る。自前で補間しない。

        private static Vector2 ActorAt(PlaybackPlay play, FieldPosition pos, double t)
        {
            var p = PlaybackEvaluator.FielderAt(play, pos, t).Pos;
            return new Vector2((float)p.X, (float)p.Y);
        }

        private static Vector2? RunnerAt(PlaybackPlay play, string label, double t)
        {
            foreach (var r in play.Runners)
            {
                if (r.Label != label) continue;
                var p = PlaybackEvaluator.RunnerAt(r, t);
                return p.HasValue ? new Vector2((float)p.Value.X, (float)p.Value.Y) : (Vector2?)null;
            }
            return null;
        }

        private static string Vec(Vector3 v)
            => ("(" + v.x.ToString("F1") + "," + v.y.ToString("F1") + "," + v.z.ToString("F1") + ")").PadRight(20);

        private static string Mark(bool on) => on ? "○" : "・";

        private static Player Guy(string name) => new()
        {
            Name = name, Position = FieldPosition.Catcher,
            Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
            Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
        };
    }
}
#endif

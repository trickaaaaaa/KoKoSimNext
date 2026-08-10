#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Unity.Match;
using UnityEditor;
using UnityEngine;

namespace KokoSim.Unity.EditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】3D守備カメラの実測（issue #566）。
    ///
    /// <para>「カメラが空の地面を向いて誰も映らない」は絵では**狙いを外しているのか寄りすぎなのか
    /// 区別できない**（design-18 §3.3 の申し送りと同型）。ここでは engine のカメラプランを
    /// <see cref="DuelStageRig.FieldFramingFor"/>（実機と同じ枠）で組み、実カメラ
    /// （<c>WorldToViewportPoint</c>）で被写体が枠に入っているかを 0.05秒刻みで数える
    /// ＝どの区間で誰が外れているかを数字で出す。</para>
    ///
    /// <para>全プレー（打球7種・盗塁・牽制・振り逃げ・暴投）を同じ物差しで並べるのが要点。
    /// 暴投だけ直しても、他のプレーの構図が動いていたら退行なので、
    /// <b>変更の有無まで含めて1枚の表にする</b>（issue #566 の完了条件）。</para>
    ///
    /// <para>Play に入らない（Edit mode）。リグも建てない＝<see cref="DuelStageRig"/> の
    /// 据え位置・枠の定数だけを読んで同じ計算を再現する。</para>
    /// </summary>
    public static class FieldCameraProbe
    {
        // 3D守備ビューのRT（DuelStageRig の既定と同じ縦横比）。枠の横幅は縦×アスペクトで決まる。
        private const int RtW = 960;
        private const int RtH = 540;
        private const double Step = 0.05;

        [MenuItem("KokoSim/Probe/守備カメラを全プレーで実測（issue #566）")]
        public static void RunAll()
        {
            var sb = new StringBuilder();
            sb.Append("守備カメラ実測（issue #566）  RT=").Append(RtW).Append('×').Append(RtH)
              .Append("  刻み ").Append(Step.ToString("F2")).Append("秒\n")
              .Append("盲点＝**そのプレーに関与している人**（走る野手・送球の投げ手/受け手・走者）が\n")
              .Append("　　　1人も枠に入っていないコマ＝issue #566 の「空の地面を映している」絵。\n")
              .Append("　　　（背景に立っているだけの野手は数えない。緩い判定では平面の枠の7倍先の外野手まで拾う）\n\n");

            // 2つの機構を分けて測る。①送球枠を伸ばす（跳びを消す）②遅れたぶん枠を広げる（塞ぐ）。
            // 「両方いる」のか「片方で足りる」のかは、入れてみないと分からない＝4通り並べる。
            var configs = new[]
            {
                ("なし         ", false, false),
                ("①伸ばす      ", true, false),
                ("②広げる      ", false, true),
                ("①+②         ", true, true),
            };

            sb.Append("プレー                        ");
            foreach (var cfg in configs) sb.Append("| ").Append(cfg.Item1).Append("     ");
            sb.Append("| 構図の差(なし→①+②)\n");
            sb.Append("                              ");
            foreach (var _ in configs) sb.Append("| 盲点   最大枠     ");
            sb.Append("| 注視ずれ 枠比\n");
            sb.Append("------------------------------");
            foreach (var _ in configs) sb.Append("+-------------------");
            sb.Append("+---------------\n");

            var detail = new StringBuilder();
            foreach (var (name, play, start) in Plays())
            {
                sb.Append(Pad(name, 30));
                Result first = null, last = null;
                foreach (var (label, grow, keep) in configs)
                {
                    var r = Measure(play, start, grow, keep,
                        detail: detail.Append("\n════ ").Append(name).Append("  【").Append(label.Trim()).Append("】\n"));
                    if (first == null) first = r;
                    last = r;
                    sb.Append("| ").Append(Blind(r)).Append(' ').Append(F(r.MaxFrameH, 6)).Append("m ");
                }
                var d = Diff(first, last);
                sb.Append("| ").Append(F(d.Aim, 6)).Append("m ").Append(F(d.ZoomRatio, 5)).Append("倍\n");
            }

            sb.Append("\n盲点＝コマ数（秒）。最大枠＝縦に収めた実寸の最大（大きいほど人が小さい。40mで人は約24px）。\n")
              .Append("注視ずれ＝「なし」と「①+②」で注視点が離れた最大距離[m]。枠比＝枠の高さの最大比（1.00＝変化なし）。\n");

            MeasureSearchScale(sb);
            sb.Append(detail);

            Debug.Log(sb.ToString());
            System.IO.Directory.CreateDirectory("out");
            System.IO.File.WriteAllText("out/field-camera-566.txt", sb.ToString());
            Debug.Log("[FieldCameraProbe] out/field-camera-566.txt に書き出した");
        }

        /// <summary>
        /// engine の平面の枠（DesignW/DesignH ÷ zoom の長方形）と、実カメラが実際に地面を覆う範囲の
        /// ずれを実測する（issue #566）。
        ///
        /// <para>engine の「枠に人が居るか」判定はこの長方形で行うしかない（<c>engine/</c> は
        /// UnityEngine を知らない＝不変条件#3）。だが実カメラは<b>斜めに見下ろす</b>ので、
        /// 地面の覆う範囲は奥行き方向に何倍にも伸びる。この倍率を知らずに等倍で判定すると、
        /// 実際には人が映っているコマまで「誰も居ない」と誤判定して枠を広げてしまう。</para>
        ///
        /// <para>出すのは「実カメラで人が見えているコマで、平面の枠を何倍にすればその人を含むか」の
        /// 分布。実測 2026-08-10 は 中央値0.25／90%点1.17／<b>最大7.24</b>＝ばらつきが大きすぎて
        /// 1つの倍率では吸収できない。よって engine 側は倍率を持たず<b>等倍のまま安全側</b>で判定する
        /// （平面の枠に居る＝実カメラにも必ず写っている。逆は言えない）。この表はその根拠。</para>
        /// </summary>
        private static void MeasureSearchScale(StringBuilder sb)
        {
            sb.Append("\n──── 平面の枠 vs 実カメラの覆う範囲（engine の判定枠に掛ける倍率の実測）\n");
            sb.Append("プレー                        | 実カメラで人が見えているコマで必要な倍率\n");
            sb.Append("                              |  中央値   90%点   最大\n");
            sb.Append("------------------------------+---------------------------\n");

            var all = new List<double>();
            foreach (var (name, play, start) in Plays())
            {
                // **連動を切った**プランで測る。入れたプランで測ると、広げた結果として人が枠内に
                // 入っている＝「等倍で足りている」としか出ず、何も分からない（自分の出力を測る罠）。
                var f0 = DuelStageRig.FieldFramingFor(play);
                var framing = new CameraFraming(f0.MaxZoom, f0.MarginM, f0.PitchZoom,
                    f0.ThrowFollowsBall, f0.HeldBallFollowsRunner, f0.LooseBallFollowsChaser,
                    ThrowBoxGrowsWithBall: false, KeepSubjectFramed: false);
                var plan = CameraPlanBuilder.Build(play, framing, start);
                var wideStart = CameraPlanBuilder.WideStartTime(play);
                var shownEnd = double.IsFinite(wideStart) ? System.Math.Min(play.Dur, wideStart - 0.3) : play.Dur;
                var cam = NewProbeCamera();
                var ks = new List<double>();

                for (var t = (double)start; t <= shownEnd + 1e-9; t += Step)
                {
                    ComputeFrame(plan, play, (float)t, out var aim, out var frameH);
                    AimCamera(cam, aim, frameH);
                    if (!AnyFielderInFrame(cam, play, t) && !AnyRunnerInFrame(cam, play, t)) continue;

                    // 平面の枠（engine と同じ定義）。中心は注視点、縦は frameH、横は 760/520 比。
                    var vp = CameraEvaluator.ViewportAt(plan, t);
                    var halfH = (float)(520.0 / 3.35 / System.Math.Max(vp.ZoomMult, 0.01)) * 0.5f;
                    var halfW = (float)(760.0 / 3.35 / System.Math.Max(vp.ZoomMult, 0.01)) * 0.5f;

                    // 実カメラで見えている人のうち、平面の枠に対していちばん内側に居る人の倍率。
                    var best = double.PositiveInfinity;
                    foreach (var p in VisibleActors(cam, play, t))
                    {
                        var k = Mathf.Max(Mathf.Abs(p.x - (float)vp.Cx) / halfW,
                                          Mathf.Abs(p.y - (float)vp.Cy) / halfH);
                        if (k < best) best = k;
                    }
                    if (double.IsFinite(best)) { ks.Add(best); all.Add(best); }
                }
                Object.DestroyImmediate(cam.gameObject);
                sb.Append(Pad(name, 30)).Append("| ").Append(Q(ks, 0.5)).Append("  ").Append(Q(ks, 0.9))
                  .Append("  ").Append(Q(ks, 1.0)).Append("   （").Append(ks.Count).Append("コマ）\n");
            }
            sb.Append(Pad("＝全プレー", 30)).Append("| ").Append(Q(all, 0.5)).Append("  ").Append(Q(all, 0.9))
              .Append("  ").Append(Q(all, 1.0)).Append("   （").Append(all.Count).Append("コマ）\n")
              .Append("倍率1.0＝平面の枠のとおり。大きいほど「実カメラは見えているのに平面の枠では外」。\n");
        }

        private static string Q(List<double> xs, double q)
        {
            if (xs.Count == 0) return "  —  ";
            xs.Sort();
            var i = Mathf.Clamp(Mathf.RoundToInt((float)(q * (xs.Count - 1))), 0, xs.Count - 1);
            return xs[i].ToString("F2").PadLeft(5);
        }

        private static IEnumerable<Vector2> VisibleActors(Camera cam, PlaybackPlay play, double t)
        {
            foreach (FieldPosition pos in System.Enum.GetValues(typeof(FieldPosition)))
            {
                if (pos == FieldPosition.DesignatedHitter) continue;
                var p = ActorAt(play, pos, t);
                if (InFrame(cam, p)) yield return p;
            }
            foreach (var r in play.Runners)
            {
                var rp = PlaybackEvaluator.RunnerAt(r, t);
                if (!rp.HasValue) continue;
                var p = new Vector2((float)rp.Value.X, (float)rp.Value.Y);
                if (InFrame(cam, p)) yield return p;
            }
        }

        // ── 素材（実機が実際に組むプレー） ──

        /// <summary>
        /// 測る対象。打球7種は <see cref="PlaybackSamples.All"/>（ゴールデンの素材）、
        /// 打席途中プレーは <see cref="TimelineBuilder"/> の実出力。
        /// <c>start</c> は守備ビューが見せ始める時刻（<see cref="DuelStageRig"/> と同じ考え方）。
        /// </summary>
        private static IEnumerable<(string Name, PlaybackPlay Play, float Start)> Plays()
        {
            foreach (var p in PlaybackSamples.All)
                yield return (p.Name, p, 0f);

            var field = new FieldGeometry();
            var c = new BaserunningCoefficients();

            yield return ("⑧ 二盗（セーフ）",
                Adapt(TimelineBuilder.BuildSteal(Guy("走一"), Catcher(), StealResult.Safe, c, field)), 0f);
            yield return ("⑨ 二盗（刺す）",
                Adapt(TimelineBuilder.BuildSteal(Guy("走一"), Catcher(), StealResult.CaughtStealing, c, field)), 0f);
            yield return ("⑩ 牽制（一塁・帰塁）",
                Adapt(TimelineBuilder.BuildPickoff(Guy("走一"), Guy("投手"), "first", false, c, field)), 0f);
            yield return ("⑪ 振り逃げ",
                Adapt(TimelineBuilder.BuildDropThirdStrike(Guy("打者"), Catcher(), c, field)), 0f);
            yield return ("⑫ 暴投・捕逸（一・三塁）",
                Adapt(TimelineBuilder.BuildBatteryMiss(Guy("走一"), null, Guy("走三"), Catcher(), c, field)), 0f);
        }

        private static PlaybackPlay Adapt(PlayTimeline tl) => PlayTimelineAdapter.ToPlaybackPlay(tl);

        // ── 計測 ──

        private sealed class Result
        {
            public int Blind;
            public int Total;
            public double BlindFrom = -1, BlindTo = -1;
            public float MaxFrameH;
            public double MaxPan;
            public readonly List<(double T, Vector3 Aim, float FrameH)> Frames = new List<(double, Vector3, float)>();
        }

        private static Result Measure(PlaybackPlay play, float start, bool grow, bool keepFramed, StringBuilder detail)
        {
            // 実機と同じ枠を使う（DuelStageRig が単一ソース）。切り分けのため2つの機構だけ差し替える。
            // （Unity側は C#9 なので構造体の with 式が使えない＝メンバを並べて組み直す）
            var f0 = DuelStageRig.FieldFramingFor(play);
            var framing = new CameraFraming(f0.MaxZoom, f0.MarginM, f0.PitchZoom,
                f0.ThrowFollowsBall, f0.HeldBallFollowsRunner, f0.LooseBallFollowsChaser,
                ThrowBoxGrowsWithBall: grow, KeepSubjectFramed: keepFramed);
            var plan = CameraPlanBuilder.Build(play, framing, start);

            // 実際に見せる長さ（issue #522 の打ち切り）。ここより後ろは再生されないので数えない。
            var wideStart = CameraPlanBuilder.WideStartTime(play);
            var shownEnd = double.IsFinite(wideStart) ? System.Math.Min(play.Dur, wideStart - 0.3) : play.Dur;

            var cam = NewProbeCamera();
            var r = new Result();
            if (detail != null)
                detail.Append("  t   |  aim(x,y,z)          frameH | 画面/秒 | 枠内\n");

            var prevAim = Vector3.zero;
            var firstSample = true;
            for (var t = (double)start; t <= shownEnd + 1e-9; t += Step)
            {
                ComputeFrame(plan, play, (float)t, out var aim, out var frameH);
                AimCamera(cam, aim, frameH);

                var pan = 0.0;
                if (!firstSample)
                    pan = Vector2.Distance(new Vector2(prevAim.x, prevAim.z), new Vector2(aim.x, aim.z))
                          / Step / Mathf.Max(frameH, 0.01f);
                firstSample = false;
                prevAim = aim;
                if (pan > r.MaxPan) r.MaxPan = pan;
                if (frameH > r.MaxFrameH) r.MaxFrameH = frameH;
                r.Frames.Add(((double)t, aim, frameH));

                // issue #566 の期待は「**球を追う野手か走者の少なくとも一方**が常に画面内」。
                // 「守備9人の誰か」まで緩めると、遠くに立っているだけの外野手が背景に写っている
                // コマまで「読める」になり、症状（空の地面を映している絵）と一致しない
                // （実測 2026-08-10: 緩い判定では平面の枠の7倍先に居る野手を数えていた）。
                var readable = InvolvedInFrame(cam, play, t);
                r.Total++;
                if (!readable)
                {
                    r.Blind++;
                    if (r.BlindFrom < 0) r.BlindFrom = t;
                    r.BlindTo = t;
                }

                if (detail != null)
                    detail.Append(t.ToString("F2").PadLeft(5)).Append(" | ")
                          .Append(Vec(aim)).Append("  ").Append(frameH.ToString("F1").PadLeft(5))
                          .Append(" | ").Append(pan.ToString("F1").PadLeft(6))
                          .Append("  | ").Append(readable ? "読める" : "★誰も居ない").Append('\n');
            }

            Object.DestroyImmediate(cam.gameObject);
            return r;
        }

        /// <summary>連動なし／ありの構図の差（同じ時刻どうしを突き合わせる）。</summary>
        private static (double Aim, double ZoomRatio) Diff(Result a, Result b)
        {
            var maxAim = 0.0;
            var maxRatio = 1.0;
            var n = Mathf.Min(a.Frames.Count, b.Frames.Count);
            for (var i = 0; i < n; i++)
            {
                var d = Vector2.Distance(new Vector2(a.Frames[i].Aim.x, a.Frames[i].Aim.z),
                                         new Vector2(b.Frames[i].Aim.x, b.Frames[i].Aim.z));
                if (d > maxAim) maxAim = d;
                var ratio = b.Frames[i].FrameH / Mathf.Max(a.Frames[i].FrameH, 0.01f);
                if (ratio > maxRatio) maxRatio = ratio;
            }
            return (maxAim, maxRatio);
        }

        private static string Blind(Result r)
        {
            if (r.Blind == 0) return "  0     ";
            return (r.Blind.ToString() + "コマ").PadLeft(5) + (r.BlindTo - r.BlindFrom + Step).ToString("F2") + "s";
        }

        /// <summary>
        /// そのプレーに<b>関与している</b>人が1人でも枠内か（issue #566 の期待の判定）。
        /// 関与者＝走者（進塁している人）＋動いている野手（球を追う／カバーに入る）＋送球の受け手。
        /// 定位置に立っているだけの野手は数えない＝「背景に人が写っていれば読める」にしない。
        /// </summary>
        private static bool InvolvedInFrame(Camera cam, PlaybackPlay play, double t)
        {
            if (AnyRunnerInFrame(cam, play, t)) return true;
            // バッテリーは動かなくても常に関与者（投球区間の固定フレームは投手と捕手を見せる絵）。
            // これを外すと、どのプレーも頭の投球0.95秒が「盲点」に数えられて比較にならない。
            if (InFrame(cam, ActorAt(play, FieldPosition.Pitcher, t))) return true;
            if (InFrame(cam, ActorAt(play, FieldPosition.Catcher, t))) return true;
            if (play.Moves != null)
                foreach (var kv in play.Moves)
                {
                    var started = false;
                    foreach (var m in kv.Value) if (t >= m.T0) { started = true; break; }
                    if (started && InFrame(cam, ActorAt(play, kv.Key, t))) return true;
                }
            foreach (var seg in play.Ball)
            {
                if (seg.ToRole is { } to && InFrame(cam, ActorAt(play, to, t))) return true;
                if (seg.FromRole is { } from && InFrame(cam, ActorAt(play, from, t))) return true;
            }
            return false;
        }

        /// <summary>守備9人のうち1人でも枠内か（送球の受け手・カバーに入った野手も被写体）。</summary>
        private static bool AnyFielderInFrame(Camera cam, PlaybackPlay play, double t)
        {
            foreach (FieldPosition pos in System.Enum.GetValues(typeof(FieldPosition)))
            {
                if (pos == FieldPosition.DesignatedHitter) continue;
                if (InFrame(cam, ActorAt(play, pos, t))) return true;
            }
            return false;
        }

        private static bool AnyRunnerInFrame(Camera cam, PlaybackPlay play, double t)
        {
            foreach (var r in play.Runners)
            {
                var p = PlaybackEvaluator.RunnerAt(r, t);
                if (p.HasValue && InFrame(cam, new Vector2((float)p.Value.X, (float)p.Value.Y))) return true;
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

        /// <summary>据え位置は被写体の深さで内野/外野を切り替える。</summary>
        private static Vector3 StationFor(Vector3 aim)
            => aim.z >= 45f ? DuelStageRig.FieldStationOutfield : DuelStageRig.FieldStationInfield;

        private static Camera NewProbeCamera()
        {
            var go = new GameObject("~FieldCameraProbe") { hideFlags = HideFlags.HideAndDontSave };
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

        /// <summary>地面の点（x,z）が枠内か。人の腰の高さ（0.9m）で見る。</summary>
        private static bool InFrame(Camera cam, Vector2 p)
        {
            var v = cam.WorldToViewportPoint(new Vector3(p.x, 0.9f, p.y));
            return v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
        }

        // 位置は再生評価器（engine の純関数）から引く＝描画と同じ位置を見る。自前で補間しない。
        private static Vector2 ActorAt(PlaybackPlay play, FieldPosition pos, double t)
        {
            var p = PlaybackEvaluator.FielderAt(play, pos, t).Pos;
            return new Vector2((float)p.X, (float)p.Y);
        }

        private static string Vec(Vector3 v)
            => ("(" + v.x.ToString("F1") + "," + v.y.ToString("F1") + "," + v.z.ToString("F1") + ")").PadRight(20);

        private static string Pad(string s, int n) => s.Length >= n ? s.Substring(0, n) : s.PadRight(n);
        private static string F(double v, int n) => v.ToString("F2").PadLeft(n);

        private static Player Catcher() => Guy("捕手", FieldPosition.Catcher);

        private static Player Guy(string name, FieldPosition pos = FieldPosition.Catcher) => new Player
        {
            Name = name, Position = pos,
            Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
            Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
        };
    }
}
#endif

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】ファウルの打球が画面の中で跳ね返って見える件の実測（issue #1074・#1543・#1766）。
    ///
    /// <para>実試合（engine をヘッドレスで回す）のファウルを全部拾い、<b>対決ビュー</b>の経路（<see cref="DuelStageRig.OnPitchReleased"/>
    /// → <see cref="DuelStageRig.Tick"/>）と、守備ビューへ切り替わるファウル（<see cref="FoulViewKind"/> が None 以外）の
    /// <b>守備ビュー</b>の経路（<see cref="DuelStageRig.BeginFieldPlay"/> → <see cref="DuelStageRig.SetFieldTime"/>）を、
    /// MatchLiveController と同じ順序で再生して、毎コマの球の<b>画面上の位置</b>（対決カメラ／守備カメラのビューポート）を測る。
    /// 見た目の問題なので世界座標でなく画面座標で測る（カメラが動けば、球が真っ直ぐ飛んでいても画面では折り返す）。</para>
    ///
    /// <para>拾う条件: ①水平に往復（進んだ向きと逆へ戻った px）②画面から出て戻る ③落ちてから上がる（高さ[m]で測り、
    /// 地面のバウンドと区別するため最大の跳ね上がりを出す）。結果は <c>out/foul-rebound-1766/report.txt</c>。
    /// 呼び方: MCP execute_code で <c>KokoSim.UnityEditorTools.FoulReboundProbe.Run(100, 1)</c>。</para>
    /// </summary>
    public static class FoulReboundProbe
    {
        private const string Dir = "out/foul-rebound-1766";
        private const int W = 960;
        private const int H = 540;
        private const float Dt = 1f / 60f;

        /// <summary>水平に戻った量がこの px を超えたら拾う（画面 960 幅で 1px 未満の揺れは数えない）。</summary>
        public const float HorizontalBackPx = 8f;

        private static string KindOf(PlaybackBallSegment seg) => seg is EndpointBallSegment e ? "/" + e.Kind : "";

        public sealed class Foul
        {
            public int Seed, Pa, Pitch;
            public PlaybackPlay Play;
            public PlaybackBallSegment Batted;
            public float PlateX, PlateY;
            public float Apex, Dist, Hang, Speed;   // 打球の頂点高さ[m]・着地までの水平距離[m]・滞空[s]・平均水平速度[m/s]
            public string Describe() =>
                Batted == null ? $"seed={Seed} pa={Pa} p={Pitch} 打球なし plate=({PlateX:F2},{PlateY:F2})"
                    : $"seed={Seed} pa={Pa} p={Pitch} view={Play.FoulView} {Batted.GetType().Name}{KindOf(Batted)} apex={Apex:F1}m dist={Dist:F1}m hang={Hang:F2}s v={Speed:F1}m/s T={Batted.T0:F2}-{Batted.T1:F2} plate=({PlateX:F2},{PlateY:F2})";
        }

        public struct Sample
        {
            public float T;
            public bool Visible;
            public float X, Y;       // 画面 px（左上原点）
            public float HeightM;
            public float Depth;
            public float FrameH, AimY;
            public float LX, LZ;     // ステージローカルの水平位置[m]（カメラの動きを除いた球そのものの位置）
        }

        public sealed class Finding
        {
            public string View;      // duel / field
            public float BackPx;
            public float BackAt;
            public int Reentries;
            public float VerticalRiseM;
            public float VerticalRiseAt;
            public int VisibleFrames;
            public string ExitSide = "-";
            public float WorldBackM, WorldBackAt;   // 球そのもの（世界）の水平の戻り[m]。カメラの動きは含まない
            public bool IsBad => BackPx > HorizontalBackPx || Reentries > 0 || WorldBackM > 0.05f;
        }

        // ---- 実試合のファウルを拾う ----

        public static List<Foul> CollectFouls(int games, ulong seed0)
        {
            var list = new List<Foul>();
            for (int g = 0; g < games; g++)
            {
                ulong seed = seed0 + (ulong)g;
                var rng = new Xoshiro256Random(seed);
                var away = StrengthTeamFactory.Create(60.0, "遠征校", rng.Fork(1));
                var home = StrengthTeamFactory.Create(60.0, "地元校", rng.Fork(2));
                var ctx = new GameContext { CaptureTimelines = true, CapturePitches = true };
                var prog = new MatchProgression(away, home, ctx, seed);
                int pa = 0;
                while (!prog.IsFinished)
                {
                    if (!prog.Advance()) break;
                    var cur = prog.Current;
                    if (cur == null) break;
                    pa++;
                    for (int i = 0; i < cur.PitchSeq.Pitches.Count; i++)
                    {
                        var pt = cur.PitchSeq.Pitches[i];
                        if (pt.Kind != PitchKind.Foul) continue;
                        var seg = pt.FoulFlight == null ? null : BattedSegmentOf(pt.FoulFlight);
                        if (seg == null && pt.FoulFlight != null) continue;
                        list.Add(new Foul
                        {
                            Seed = (int)seed, Pa = pa, Pitch = i, Play = pt.FoulFlight, Batted = seg,
                            PlateX = (float)(pt.LocationX ?? 0.0), PlateY = (float)(pt.LocationY ?? 0.75),
                        });
                        if (seg != null) Characterize(list[list.Count - 1]);
                    }
                }
            }
            return list;
        }

        private static void Characterize(Foul f)
        {
            var seg = f.Batted;
            double apex = 0;
            const int N = 40;
            for (int i = 0; i <= N; i++) apex = Math.Max(apex, seg.At((seg.T1 - seg.T0) * i / N).H);
            var a = seg.At(0); var b = seg.At(seg.T1 - seg.T0);
            f.Apex = (float)apex;
            f.Hang = (float)(seg.T1 - seg.T0);
            f.Dist = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            f.Speed = f.Hang > 1e-3f ? f.Dist / f.Hang : 0f;
        }

        /// <summary>打球タイムラインの先頭は投球セグメント。その次が打球（MatchLiveController.BattedOf と同じ）。</summary>
        public static PlaybackBallSegment BattedSegmentOf(PlaybackPlay play)
        {
            foreach (var s in play.Ball)
            {
                if (s is PitchSegment) continue;
                if (s is EndpointBallSegment eb && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Pitch) continue;
                return s;
            }
            return null;
        }

        // ---- 再生 ----

        public static DuelStageRig NewRig()
        {
            var rig = new DuelStageRig();
            if (!rig.Available) { rig.Dispose(); return null; }
            rig.EnsureSize(W, H);
            for (int i = 0; i < 24; i++) rig.Tick(1f / DuelStageRig.PoseFps);
            return rig;
        }

        private static Sample Snap(DuelStageRig rig, float t)
        {
            var local = rig.BallLocalForCapture;
            var vp = rig.BallViewportForCapture;
            if (local == null || vp == null) return new Sample { T = t, Visible = false };
            var v = vp.Value;
            bool inFrame = v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
            return new Sample { T = t, Visible = inFrame, X = v.x * W, Y = (1f - v.y) * H, HeightM = local.Value.y, Depth = v.z, LX = local.Value.x, LZ = local.Value.z, FrameH = rig.FieldFrameShownForCapture.FrameH, AimY = rig.FieldFrameShownForCapture.AimY };
        }

        /// <summary>
        /// 1本のファウルを MatchLiveController と同じ順序で流す。守備ビューで見せるファウルは、対決ビューの打球が
        /// 画面から出る（または保険の秒数）まで対決ビューで見せ、その続きを守備ビューで見せる。
        /// </summary>
        public static void Replay(DuelStageRig rig, Foul f, List<Sample> duel, List<Sample> field, List<Sample> pre = null)
        {
            var pitch = new DuelPitchFlight(f.PlateX, f.PlateY, f.PlateX, f.PlateY, 0.5f, 0.05f, 0.2f, 140f);
            rig.OnPitchReleased(-1f, DuelPitchOutcome.FoulContact, pitch, f.Batted);
            if (f.Batted == null)
            {
                // 打球を持たないファウル（バント系など）: リリースから 1.6 秒の球の動きをそのまま測る
                for (float c = 0f; c < 1.6f; c += Dt) { duel.Add(Snap(rig, c)); rig.Tick(Dt); }
                rig.HideBall();
                return;
            }
            for (int i = 0; i < 400 && rig.BattedElapsedSeconds < 0f; i++) { if (pre != null) pre.Add(Snap(rig, i * Dt)); rig.Tick(Dt); }
            bool toField = f.Play.FoulView != FoulViewKind.None;
            float clock = 0f;
            float safety = 1.6f;
            while (clock < safety)
            {
                if (toField && rig.BattedOutOfFrame) break;
                duel.Add(Snap(rig, clock));
                rig.Tick(Dt);
                clock += Dt;
            }
            if (!toField) { rig.HideBall(); return; }

            // 対決ビューで見せたぶんを飛ばして、守備ビューは続きから（MatchLiveController.ContactMomentOf）
            float shown = rig.BattedElapsedSeconds;
            var head = f.Play.Ball[0];
            double t1 = head.T1;
            double? nextCap = null;
            foreach (var cap in f.Play.Caps) if (cap.T > t1 && (!nextCap.HasValue || cap.T < nextCap.Value)) nextCap = cap.T;
            double start = nextCap ?? t1;
            if (shown > 0) start = Math.Max(start, t1 + shown);

            double last = f.Play.ResAt;
            foreach (var s in f.Play.Ball) if (s.T1 > last) last = s.T1;
            if (f.Play.Moves != null) foreach (var kv in f.Play.Moves) foreach (var m in kv.Value) if (m.T1 > last) last = m.T1;
            double end = Math.Min(f.Play.Dur, last + 0.6 + 1.0);

            rig.BeginFieldPlay(f.Play, (float)start);
            float ft = (float)start;
            float t0 = ft;
            while (ft < end)
            {
                rig.SetFieldTime(ft);
                rig.Tick(Dt);
                field.Add(Snap(rig, ft - t0));
                ft += Dt;
            }
            rig.EndFieldPlay();
            rig.HideBall();
        }

        // ---- 解析 ----

        public static Finding Analyze(string view, List<Sample> s)
        {
            var r = new Finding { View = view };
            // 水平の往復: 見えているコマの x の動きのうち、全体の進む向きと逆へ戻った合計 px
            var vis = new List<Sample>();
            bool wasVisible = false, wasHidden = false;
            float lastLX = 0f, lastLZ = 0f, lastH = 0f;
            foreach (var x in s)
            {
                if (x.Visible)
                {
                    if (wasHidden && wasVisible) r.Reentries++;
                    // 止まった球（着地後）が画面の中で動くのはカメラの寄り直し＝球の跳ね返りではないので、水平の戻りの測定からは外す
                    bool still = vis.Count > 0 && Mathf.Abs(x.HeightM - lastH) < 0.02f
                        && Mathf.Abs(x.LX - lastLX) < 0.02f && Mathf.Abs(x.LZ - lastLZ) < 0.02f;
                    lastLX = x.LX; lastLZ = x.LZ; lastH = x.HeightM;
                    wasVisible = true;
                    wasHidden = false;
                    if (still) continue;
                    vis.Add(x);
                    wasVisible = true;
                    wasHidden = false;
                }
                else if (wasVisible) wasHidden = true;
            }
            r.VisibleFrames = vis.Count;
            for (int i = 0; i < s.Count; i++)
            {
                if (s[i].Visible || s[i].Depth == 0f) continue;
                if (i == 0 || !s[i - 1].Visible) continue;
                r.ExitSide = s[i].Y < 0f ? "上" : s[i].Y > H ? "下" : s[i].X < 0f ? "左" : "右";
                break;
            }
            if (vis.Count >= 2)
            {
                // コマごとの細かい揺れ（カメラの追従のガタつき）は「跳ね返り」ではないので、前後4コマの移動平均で均してから測る
                var sm = new float[vis.Count];
                for (int i = 0; i < vis.Count; i++)
                {
                    float acc = 0f; int n = 0;
                    for (int k = Mathf.Max(0, i - 4); k <= Mathf.Min(vis.Count - 1, i + 4); k++) { acc += vis[k].X; n++; }
                    sm[i] = acc / n;
                }
                float net = sm[sm.Length - 1] - sm[0];
                float dir = Mathf.Abs(net) < 1e-3f ? 0f : Mathf.Sign(net);
                if (dir == 0f)
                {
                    // 正味ゼロ＝進んでから同じだけ戻った。最初に動いた向きを「進む向き」にする
                    for (int i = 1; i < sm.Length; i++)
                        if (Mathf.Abs(sm[i] - sm[0]) > 1f) { dir = Mathf.Sign(sm[i] - sm[0]); break; }
                }
                float sum = 0f, worst = 0f;
                for (int i = 1; i < sm.Length; i++)
                {
                    float along = (sm[i] - sm[i - 1]) * dir;
                    if (along < 0f) { sum -= along; if (-along > worst) { worst = -along; r.BackAt = vis[i].T; } }
                }
                r.BackPx = sum;
            }
            // 球そのものの水平の戻り（画面ではなくステージ上）。見えているかどうかに関係なく、描かれている全コマで測る
            var drawn = new List<Sample>();
            foreach (var x in s) if (x.Depth != 0f || x.Visible) drawn.Add(x);
            if (drawn.Count >= 2)
            {
                var a = drawn[0]; var b = drawn[drawn.Count - 1];
                var d = new Vector2(b.LX - a.LX, b.LZ - a.LZ);
                if (d.magnitude > 0.05f)
                {
                    d.Normalize();
                    float sumW = 0f, worstW = 0f;
                    for (int i = 1; i < drawn.Count; i++)
                    {
                        float along = Vector2.Dot(new Vector2(drawn[i].LX - drawn[i - 1].LX, drawn[i].LZ - drawn[i - 1].LZ), d);
                        if (along < 0f) { sumW -= along; if (-along > worstW) { worstW = -along; r.WorldBackAt = drawn[i].T; } }
                    }
                    r.WorldBackM = sumW;
                }
            }
            // 落ちてから上がる（高さ[m]）: 最初に頂点を過ぎた後の最大の跳ね上がり
            if (vis.Count >= 3)
            {
                float peak = float.MinValue; int peakIdx = 0;
                for (int i = 0; i < vis.Count; i++) if (vis[i].HeightM > peak) { peak = vis[i].HeightM; peakIdx = i; }
                float minAfter = float.MaxValue;
                for (int i = peakIdx; i < vis.Count; i++)
                {
                    minAfter = Mathf.Min(minAfter, vis[i].HeightM);
                    float rise = vis[i].HeightM - minAfter;
                    if (rise > r.VerticalRiseM) { r.VerticalRiseM = rise; r.VerticalRiseAt = vis[i].T; }
                }
            }
            return r;
        }



        /// <summary>
        /// 均さない生のコマ列で、画面上の進行方向（最初→最後に見えたコマ）と逆へ動いた量[px]・奥行きが縮む→伸びる反転を測る。
        /// 速いライナーは数コマで画面を出るので、移動平均（±4コマ）をかけると反転ごと消える（issue #1766）。
        /// </summary>
        public static (float backPx, float backAt, float backYpx, float backYAt, int depthFlips, int visFrames) AnalyzeRaw(List<Sample> s)
        {
            var vis = new List<Sample>();
            foreach (var x in s) if (x.Visible) vis.Add(x);
            if (vis.Count < 2) return (0, 0, 0, 0, 0, vis.Count);
            var net = new Vector2(vis[vis.Count - 1].X - vis[0].X, vis[vis.Count - 1].Y - vis[0].Y);
            if (net.magnitude < 1e-3f) net = new Vector2(1, 0);
            net.Normalize();
            float sum = 0f, worst = 0f, at = 0f;
            for (int i = 1; i < vis.Count; i++)
            {
                float along = Vector2.Dot(new Vector2(vis[i].X - vis[i - 1].X, vis[i].Y - vis[i - 1].Y), net);
                if (along < 0f) { sum -= along; if (-along > worst) { worst = -along; at = vis[i].T; } }
            }
            // 縦（画面の上下）だけの戻り: 最初に動いた縦の向きに対して
            float dy = vis[vis.Count - 1].Y - vis[0].Y;
            float dirY = Mathf.Abs(dy) < 1e-3f ? 0f : Mathf.Sign(dy);
            float sumY = 0f, worstY = 0f, atY = 0f;
            for (int i = 1; i < vis.Count; i++)
            {
                float along = (vis[i].Y - vis[i - 1].Y) * dirY;
                if (along < 0f) { sumY -= along; if (-along > worstY) { worstY = -along; atY = vis[i].T; } }
            }
            int flips = 0; float lastSign = 0f;
            for (int i = 1; i < vis.Count; i++)
            {
                float dd = vis[i].Depth - vis[i - 1].Depth;
                if (Mathf.Abs(dd) < 1e-4f) continue;
                float sg = Mathf.Sign(dd);
                if (lastSign != 0f && sg != lastSign) flips++;
                lastSign = sg;
            }
            return (sum, at, sumY, atY, flips, vis.Count);
        }

        /// <summary>
        /// ライナー性のファウル（打球の 頂点が <paramref name="maxApex"/> m 以下・平均水平速度が <paramref name="minSpeed"/> m/s 以上）だけを
        /// 対決ビューで再生し、均さない生のコマで測る（issue #1766）。<paramref name="dump"/> 本までは毎コマを出す。
        /// </summary>
        public static string RunLiners(int games = 100, ulong seed0 = 1, float maxApex = 8f, float minSpeed = 25f, int dump = 6)
        {
            Directory.CreateDirectory(Dir);
            var fouls = CollectFouls(games, seed0);
            var rig = NewRig();
            if (rig == null) return "DuelStageRig が組めない";
            var sb = new StringBuilder();
            int n = 0, bad = 0, shown = 0;
            var lines = new StringBuilder();
            var all = new StringBuilder();
            try
            {
                foreach (var f in fouls)
                {
                    if (f.Batted == null || f.Apex > maxApex || f.Speed < minSpeed) continue;
                    var duel = new List<Sample>(); var field = new List<Sample>(); var pre = new List<Sample>();
                    Replay(rig, f, duel, field, pre);
                    for (int i = 0; i < 90; i++) rig.Tick(Dt);
                    n++;
                    var joined = new List<Sample>(pre); joined.AddRange(duel);
                    var rj = AnalyzeRaw(joined);
                    var r = AnalyzeRaw(duel);
                    var a = Analyze("duel", duel);
                    bool isBad = r.backPx > 2f || r.backYpx > 2f || r.depthFlips > 0 || a.IsBad || rj.backPx > 2f;
                    if (isBad) bad++;
                    string tag = isBad ? "BAD " : "ok  ";
                    string line = $"{tag}{f.Describe()} 生戻り{r.backPx:F1}px@{r.backAt:F3} 縦戻り{r.backYpx:F1}px@{r.backYAt:F3} 奥行反転{r.depthFlips} 見えたコマ{r.visFrames} 総コマ{duel.Count} 世界戻り{a.WorldBackM:F2}m 再入{a.Reentries} 出た側{a.ExitSide} 投球込み生戻り{rj.backPx:F1}px@{rj.backAt:F3} 縦戻り{rj.backYpx:F1}px 反転{rj.depthFlips} 投球コマ{pre.Count}";
                    lines.AppendLine(line);
                    if (isBad && shown < dump)
                    {
                        shown++;
                        all.AppendLine(line);
                        all.AppendLine("T\tvis\tpxX\tpxY\theightM\tlocalX\tlocalZ\tdepth");
                        foreach (var x in pre) all.AppendLine($"{x.T - pre.Count * Dt:F3}p\t{(x.Visible ? 1 : 0)}\t{x.X:F1}\t{x.Y:F1}\t{x.HeightM:F2}\t{x.LX:F2}\t{x.LZ:F2}\t{x.Depth:F1}");
                        foreach (var x in duel) all.AppendLine($"{x.T:F3}\t{(x.Visible ? 1 : 0)}\t{x.X:F1}\t{x.Y:F1}\t{x.HeightM:F2}\t{x.LX:F2}\t{x.LZ:F2}\t{x.Depth:F1}");
                    }
                }
            }
            finally { rig.Dispose(); }
            sb.AppendLine($"ライナー性ファウル（頂点<={maxApex}m・平均水平速度>={minSpeed}m/s）: ファウル全 {fouls.Count} 本中 {n} 本、うち反転あり {bad} 本");
            sb.AppendLine(lines.ToString());
            sb.AppendLine(all.ToString());
            File.WriteAllText(Path.Combine(Dir, "liners.txt"), sb.ToString());
            return sb.ToString().Length > 6000 ? sb.ToString().Substring(0, 6000) + "…（全文は out/foul-rebound-1766/liners.txt）" : sb.ToString();
        }

        /// <summary>
        /// 追ったファウルフライ（守備ビューで見せる）を engine の組むタイムラインで作る（落下に 0.2 秒遅れて捕手が着く形）。
        /// 実試合を回さずに高いフライだけを試せる。
        /// </summary>
        public static Foul SyntheticChasedFoul(float ev, float la, float br)
        {
            var ball = new KokoSim.Engine.Match.Batting.BattedBall { ExitVelocityMps = ev, LaunchAngleDeg = la, BearingDeg = br };
            var aero = new KokoSim.Engine.Match.Pitching.Aerodynamics();
            var co = new FieldingCoefficients();
            var fg = new KokoSim.Engine.Match.Field.FieldGeometry();
            var bare = KokoSim.Engine.Match.Timeline.TimelineBuilder.BuildFoulFlight(ball, aero, co, fg, false, false, false);
            double hang = bare.Ball[1].T1 - bare.Ball[1].T0;
            var chase = new FoulChase(KokoSim.Engine.Match.Field.FieldPosition.Catcher, 0.0, -1.0, hang + 0.2, hang);
            var tl = KokoSim.Engine.Match.Timeline.TimelineBuilder.BuildFoulFlight(ball, aero, co, fg, false, false, false, chase);
            var play = KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl);
            return new Foul { Play = play, Batted = BattedSegmentOf(play), PlateX = 0.1f, PlateY = 0.8f };
        }

        /// <summary>合成した追ったフライの毎コマ（原因の切り分け用）。</summary>
        public static string TraceSynthetic(float ev, float la, float br, int every = 6)
        {
            var rig = NewRig();
            if (rig == null) return "rig 無し";
            var sb = new StringBuilder();
            try
            {
                var f = SyntheticChasedFoul(ev, la, br);
                var duel = new List<Sample>(); var field = new List<Sample>();
                Replay(rig, f, duel, field);
                sb.AppendLine("T\tvis\tpxX\tpxY\theightM\tlocalX\tlocalZ\tframeH\taimY");
                for (int i = 0; i < field.Count; i += every)
                {
                    var x = field[i];
                    sb.AppendLine($"{x.T:F2}\t{(x.Visible ? 1 : 0)}\t{x.X:F1}\t{x.Y:F1}\t{x.HeightM:F1}\t{x.LX:F1}\t{x.LZ:F1}\t{x.FrameH:F1}\t{x.AimY:F1}");
                }
            }
            finally { rig.Dispose(); }
            return sb.ToString();
        }

        /// <summary>1本の毎コマの位置を出す（原因の切り分け用）。duel / field の両方。</summary>
        public static string Trace(int seed, int pa, int pitch)
        {
            var rig = NewRig();
            if (rig == null) return "rig 無し";
            var sb = new StringBuilder();
            try
            {
                foreach (var f in CollectFouls(1, (ulong)seed))
                {
                    if (f.Pa != pa || f.Pitch != pitch) continue;
                    var duel = new List<Sample>(); var field = new List<Sample>();
                    Replay(rig, f, duel, field);
                    sb.AppendLine(f.Describe());
                    sb.AppendLine("view\tT\tvis\tpxX\tpxY\theightM\tlocalX\tlocalZ\tdepth\tframeH\taimY");
                    foreach (var x in duel) sb.AppendLine($"duel\t{x.T:F3}\t{(x.Visible ? 1 : 0)}\t{x.X:F1}\t{x.Y:F1}\t{x.HeightM:F2}\t{x.LX:F2}\t{x.LZ:F2}\t{x.Depth:F1}\t{x.FrameH:F1}\t{x.AimY:F1}");
                    foreach (var x in field) sb.AppendLine($"field\t{x.T:F3}\t{(x.Visible ? 1 : 0)}\t{x.X:F1}\t{x.Y:F1}\t{x.HeightM:F2}\t{x.LX:F2}\t{x.LZ:F2}\t{x.Depth:F1}\t{x.FrameH:F1}\t{x.AimY:F1}");
                }
            }
            finally { rig.Dispose(); }
            return sb.ToString();
        }

        /// <summary>実試合 <paramref name="games"/> 試合のファウルを再生して測り、レポートを書く。返り値はレポート本文。</summary>
        public static string Run(int games = 100, ulong seed0 = 1)
        {
            Directory.CreateDirectory(Dir);
            var fouls = CollectFouls(games, seed0);
            var rig = NewRig();
            if (rig == null) return "DuelStageRig が組めない（素体/クリップ未導入）";
            var sb = new StringBuilder();
            int duelN = 0, fieldN = 0, duelBad = 0, fieldBad = 0, none = 0, chased = 0, big = 0, noBall = 0;
            var badLines = new StringBuilder();
            try
            {
                foreach (var f in fouls)
                {
                    var duel = new List<Sample>();
                    var field = new List<Sample>();
                    Replay(rig, f, duel, field);
                    // ボールの出入りで次の球へ持ち越さないよう、少し空回しして構えへ戻す
                    for (int i = 0; i < 90; i++) rig.Tick(Dt);
                    if (f.Play == null) noBall++;
                    else if (f.Play.FoulView == FoulViewKind.None) none++;
                    else if (f.Play.FoulView == FoulViewKind.BigFoul) big++;
                    else chased++;

                    var d = Analyze("duel", duel);
                    duelN++;
                    if (d.IsBad) { duelBad++; badLines.AppendLine($"[対決] {f.Describe()} 戻り{d.BackPx:F1}px@{d.BackAt:F2}s 世界戻り{d.WorldBackM:F2}m@{d.WorldBackAt:F2}s 再入{d.Reentries}(出た側{d.ExitSide}) 跳ね{d.VerticalRiseM:F2}m@{d.VerticalRiseAt:F2}s 見えたコマ{d.VisibleFrames}"); }
                    if (field.Count > 0)
                    {
                        var fl = Analyze("field", field);
                        fieldN++;
                        if (fl.IsBad) { fieldBad++; badLines.AppendLine($"[守備] {f.Describe()} 戻り{fl.BackPx:F1}px@{fl.BackAt:F2}s 世界戻り{fl.WorldBackM:F2}m@{fl.WorldBackAt:F2}s 再入{fl.Reentries}(出た側{fl.ExitSide}) 跳ね{fl.VerticalRiseM:F2}m@{fl.VerticalRiseAt:F2}s 見えたコマ{fl.VisibleFrames}"); }
                    }
                }
            }
            finally { rig.Dispose(); }

            sb.AppendLine($"試合 {games}（seed {seed0}〜）・ファウル {fouls.Count} 本（通常 {none} / 打球なし {noBall} / 追ったフライ {chased} / 大ファウル {big}）");
            sb.AppendLine($"対決ビュー: {duelN} 本中 {duelBad} 本が水平に {HorizontalBackPx}px 超戻る／画面へ再入");
            sb.AppendLine($"守備ビュー: {fieldN} 本中 {fieldBad} 本が同様");
            sb.AppendLine(badLines.ToString());
            File.WriteAllText(Path.Combine(Dir, "report.txt"), sb.ToString());
            return sb.ToString();
        }
    }
}
#endif

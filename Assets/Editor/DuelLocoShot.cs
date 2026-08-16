#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】守備の走りの2軸（速さ×向き）と発進の確認撮影
    /// （issue wire-defender-locomotion-motions）。使い捨て。
    ///
    /// <para>engine の実試合（<see cref="MatchProgression"/>）からプレーを流し、DuelStageRig を
    /// 実ゲームと同じ 12fps で通しながら**どの野手がどの収録を流したか**を機械的に拾う。
    /// 見たい収録（背走・シャッフル・見ながら走る・発進）が出た場面だけ、その野手の**横**から
    /// 寄りの連続コマ（6コマ）を撮る。F3 の全景シートでは野手が 10〜30px で向きが読めない。</para>
    ///
    /// <para>横から撮る理由: 「胸がどちらを向いて、どちらへ動いているか」は進行方向に直交する
    /// 位置からしか分離できない（正面/背面からだと前走りも背走も同じ絵になる）。カメラは
    /// 窓の中央時刻の立ち位置に固定し、野手が枠を横切る形にする。</para>
    ///
    /// 出力: out/duel-stage/loco/*.png ／ out/duel-stage/loco/loco.tsv（コマごとの収録名・速さ・向き）
    /// </summary>
    public static class DuelLocoShot
    {
        private const string Dir = "out/duel-stage/loco";
        private const int W = 480, H = 360;
        private const int StripFrames = 8;
        private const int MaxPlates = 400;
        private const ulong Seed = 20260816UL;

        /// <summary>拾いたい収録の家族（先頭一致）。1家族につき最初の2場面を撮る。</summary>
        private static readonly string[] Families =
        {
            "Run_Back_Loop", "Walk_B_Loop", "Shuffle_L", "Shuffle_R",
            "Sprint_L90_Loop", "Sprint_R90_Loop", "Run_L90_Loop", "Run_R90_Loop",
            "Crouch_To_Run_Type01", "Crouch_To_Run_Type02", "Crouch_To_Run_L90",
            "Crouch_To_Run_R90", "Crouch_To_Run_Back",
            "Sprint_Loop", "Run_Loop",
        };
        private const int PerFamily = 2;

        private struct Hit
        {
            public PlaybackPlay Play;
            public float Contact;
            public FieldPosition Pos;
            public float T;
            public string Family;
            public int PlateIndex;
        }

        [UnityEditor.MenuItem("KokoSim/Duel/走りの向きを撮る（実試合・寄り）")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);
            foreach (var old in Directory.GetFiles(Dir, "*.png")) File.Delete(old);   // 前回の場面を残さない（拾えなかった家族が「ある」ように見える）
            var log = new StringBuilder();
            log.AppendLine("shot\tplate\tpos\tt\tclip\tspeedMul\tvx\tvz\tv\tyaw\tsideDeg\tresult");
            var accept = new StringBuilder();
            accept.AppendLine("plate\tresult\tacceptance");
            var starts = new StringBuilder();
            var legs = new StringBuilder();
            legs.AppendLine("plate\tpos\tt0\tdur\tdist\tcruise\tgapBefore");

            var rng = new Xoshiro256Random(Seed);
            var away = StrengthTeamFactory.Create(58, "A", rng);
            var home = StrengthTeamFactory.Create(58, "H", rng);
            var prog = new MatchProgression(away, home, new GameContext { CaptureTimelines = true }, Seed);

            var found = new Dictionary<string, int>();
            var hits = new List<Hit>();
            int plate = 0;
            int totalMoveFrames = 0, totalClamp = 0;
            var facingTotals = new int[5];
            int startFired = 0, startFrames = 0;
            float startSlipMax = 0f;

            var rig = new DuelStageRig();
            try
            {
                if (!rig.Available)
                {
                    Debug.LogError("[LocoShot] DuelStageRig が組めない（素体/クリップ未導入）");
                    return;
                }
                rig.EnsureSize(W, H);
                float poseStep = 1f / DuelStageRig.PoseFps;

                // ---- 1周目: 収録名を拾う ------------------------------------------------
                while (plate < MaxPlates && prog.Advance())
                {
                    plate++;
                    var pa = prog.Current;
                    var play = pa?.Play;
                    if (play == null || play.Moves == null || play.Moves.Count == 0) continue;
                    float contact = ContactOf(play);
                    if ((float)play.Dur - contact < 0.5f) continue;

                    rig.BeginFieldPlay(play, contact);
                    var seenThisPlay = new HashSet<string>();
                    for (float t = contact; t <= (float)play.Dur + 1e-4f; t += poseStep)
                    {
                        if (t > contact) rig.Tick(poseStep);
                        rig.SetFieldTime(t);
                        foreach (var pos in AllPositions)
                        {
                            string clip; float mul; Vector2 xz; float yaw;
                            if (!rig.FielderClipForCapture(pos, out clip, out mul, out xz, out yaw)) continue;
                            var fam = FamilyOf(clip);
                            if (fam == null) continue;
                            var key = fam + "|" + pos;
                            if (seenThisPlay.Contains(key)) continue;
                            int n;
                            found.TryGetValue(fam, out n);
                            if (n >= PerFamily) continue;
                            seenThisPlay.Add(key);
                            found[fam] = n + 1;
                            hits.Add(new Hit { Play = play, Contact = contact, Pos = pos, T = t, Family = fam, PlateIndex = plate });
                        }
                    }
                    var acc = rig.FielderRunDiagForCapture();
                    accept.AppendLine(plate + "\t" + pa.Result + "\t" + acc);
                    // レッグの母集団（発進を流せる余地がどれだけあるか＝engine の加減速の実態）
                    foreach (var kv in play.Moves)
                    {
                        double prevEnd = double.NegativeInfinity;
                        foreach (var m in kv.Value)
                        {
                            var from = PlaybackEvaluator.FielderAt(play, kv.Key, m.T0).Pos;
                            double dist = System.Math.Sqrt((m.To.X - from.X) * (m.To.X - from.X) + (m.To.Y - from.Y) * (m.To.Y - from.Y));
                            double dur = m.T1 - m.T0;
                            legs.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                "{0}\t{1}\t{2:F2}\t{3:F2}\t{4:F2}\t{5:F2}\t{6:F2}",
                                plate, kv.Key, m.T0, dur, dist, dur > 1e-3 ? dist / dur / (1 - PlaybackEvaluator.MoveRampFraction) : 0.0,
                                m.T0 - prevEnd));
                            prevEnd = m.T1;
                        }
                    }
                    var sp = rig.FieldStartPlanForCapture();
                    if (sp.Length > 0) starts.Append("# plate " + plate + " " + pa.Result + "\n").Append(sp);
                    Accumulate(acc, ref totalMoveFrames, ref totalClamp, facingTotals,
                        ref startFired, ref startFrames, ref startSlipMax);
                    if (found.Count >= Families.Length && AllFull(found)) break;
                }

                // engine の実試合では全レッグが全力（8.3〜8.8m/s）で背走・シャッフルの速度帯が出ない
                // （実測 2026-08-16・legs.tsv）。ゆっくり横へ流れる／下がる場面はモック（PlaybackSamples）
                // にしか無いので、拾えなかった家族はモックからも探す
                for (int mi = 0; mi < PlaybackSamples.All.Count && !AllFull(found); mi++)
                {
                    var play = PlaybackSamples.All[mi];
                    if (play == null || play.Moves == null || play.Moves.Count == 0) continue;
                    float contact = ContactOf(play);
                    int mplate = 1000 + mi;
                    rig.BeginFieldPlay(play, contact);
                    var seenThisPlay = new HashSet<string>();
                    for (float t = contact; t <= (float)play.Dur + 1e-4f; t += poseStep)
                    {
                        if (t > contact) rig.Tick(poseStep);
                        rig.SetFieldTime(t);
                        foreach (var pos in AllPositions)
                        {
                            string clip; float mul; Vector2 xz; float yaw;
                            if (!rig.FielderClipForCapture(pos, out clip, out mul, out xz, out yaw)) continue;
                            var fam = FamilyOf(clip);
                            if (fam == null) continue;
                            var key = fam + "|" + pos;
                            if (seenThisPlay.Contains(key)) continue;
                            int n;
                            found.TryGetValue(fam, out n);
                            if (n >= PerFamily) continue;
                            seenThisPlay.Add(key);
                            found[fam] = n + 1;
                            hits.Add(new Hit { Play = play, Contact = contact, Pos = pos, T = t, Family = fam, PlateIndex = mplate });
                        }
                    }
                }

                // ---- 2周目: 場面ごとに横から寄って撮る -------------------------------------
                int shot = 0;
                foreach (var h in hits)
                {
                    rig.BeginFieldPlay(h.Play, h.Contact);
                    // 窓: 見つけたコマの 0.25 秒前から 6 コマ
                    float t0 = Mathf.Max(h.Contact, h.T - 1f / DuelStageRig.PoseFps);
                    var frames = new List<Texture2D>();
                    // カメラは窓の中央時刻の立ち位置に固定。進行方向に直交する側（球から見て右手）から
                    float tMid = t0 + poseStep * (StripFrames - 1) * 0.5f;
                    var pm = PlaybackEvaluator.FielderAt(h.Play, h.Pos, tMid).Pos;
                    var pa2 = PlaybackEvaluator.FielderAt(h.Play, h.Pos, tMid - 0.1).Pos;
                    var pb2 = PlaybackEvaluator.FielderAt(h.Play, h.Pos, tMid + 0.1).Pos;
                    var v = new Vector2((float)(pb2.X - pa2.X), (float)(pb2.Y - pa2.Y)) / 0.2f;
                    var dir = v.magnitude > 0.05f ? v.normalized : new Vector2(0f, -1f);
                    var perp = new Vector2(dir.y, -dir.x);   // 進行方向の右手側
                    const float camDist = 7.5f, camH = 2.2f, frameH = 4.5f;
                    var camLocal = new Vector3((float)pm.X + perp.x * camDist, camH, (float)pm.Y + perp.y * camDist);
                    var lookLocal = new Vector3((float)pm.X, 1.0f, (float)pm.Y);

                    int k = 0;
                    for (float t = h.Contact; k < StripFrames; t += poseStep)
                    {
                        if (t > h.Contact) rig.Tick(poseStep);
                        rig.SetFieldTime(t);
                        if (t + 1e-4f < t0) continue;
                        rig.FramePreview(camLocal, lookLocal, frameH);
                        rig.RenderNow();
                        frames.Add(Grab(rig.Target));
                        string clip; float mul; Vector2 xz; float yaw;
                        rig.FielderClipForCapture(h.Pos, out clip, out mul, out xz, out yaw);
                        var pa3 = PlaybackEvaluator.FielderAt(h.Play, h.Pos, t - 1f / 60f).Pos;
                        var pb3 = PlaybackEvaluator.FielderAt(h.Play, h.Pos, t + 1f / 60f).Pos;
                        var vv = new Vector2((float)(pb3.X - pa3.X), (float)(pb3.Y - pa3.Y)) * 30f;
                        var ball = PlaybackEvaluator.BallAt(h.Play, t);
                        var here = PlaybackEvaluator.FielderAt(h.Play, h.Pos, t).Pos;
                        float side = float.NaN;
                        if (ball.HasValue && vv.magnitude > 0.05f)
                            side = Vector2.SignedAngle(vv, new Vector2((float)(ball.Value.X - here.X), (float)(ball.Value.Y - here.Y)));
                        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "{0}\t{1}\t{2}\t{3:F2}\t{4}\t{5:F2}\t{6:F2}\t{7:F2}\t{8:F2}\t{9:F0}\t{10:F0}\t{11}",
                            Name(shot, h), h.PlateIndex, h.Pos, t, clip, mul, vv.x, vv.y, vv.magnitude, yaw, side,
                            h.Play.Result));
                        k++;
                    }
                    WriteStrip(frames, Name(shot, h));
                    foreach (var f in frames) Object.DestroyImmediate(f);
                    shot++;
                }
            }
            finally
            {
                rig.Dispose();
            }

            var summary = new StringBuilder();
            summary.AppendLine("plates=" + plate + " hits=" + hits.Count);
            foreach (var f in Families)
            {
                int n; found.TryGetValue(f, out n);
                summary.AppendLine(f + "\t" + n);
            }
            summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "moveFrames={0} 目立つ={1} ({2:P0})\t向き 前{3}/左見{4}/右見{5}/背走{6}/横{7}\t発進={8}本 {9}コマ slipMax={10:F2}",
                totalMoveFrames, totalClamp, totalMoveFrames > 0 ? totalClamp / (double)totalMoveFrames : 0.0,
                facingTotals[0], facingTotals[1], facingTotals[2], facingTotals[3], facingTotals[4],
                startFired, startFrames, startSlipMax));
            File.WriteAllText(Path.Combine(Dir, "summary.txt"), summary.ToString());
            File.WriteAllText(Path.Combine(Dir, "loco.tsv"), log.ToString());
            File.WriteAllText(Path.Combine(Dir, "acceptance.tsv"), accept.ToString());
            File.WriteAllText(Path.Combine(Dir, "starts.tsv"), starts.ToString());
            File.WriteAllText(Path.Combine(Dir, "legs.tsv"), legs.ToString());
            Debug.Log("[LocoShot] DONE -> " + Dir + "\n" + summary);
        }

        private static readonly FieldPosition[] AllPositions =
        {
            FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase,
            FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop,
            FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };

        private static string FamilyOf(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return null;
            foreach (var f in Families) if (clip.StartsWith(f)) return f;
            return null;
        }

        private static bool AllFull(Dictionary<string, int> found)
        {
            foreach (var f in Families) { int n; if (!found.TryGetValue(f, out n) || n < PerFamily) return false; }
            return true;
        }

        private static string Name(int shot, Hit h)
            => shot.ToString("D2") + "-" + h.Family + "-" + h.Pos + "-p" + h.PlateIndex;

        /// <summary>FielderRunDiagForCapture の行から集計に使う数だけ抜く。</summary>
        private static void Accumulate(string acc, ref int moveFrames, ref int clamp, int[] facing,
            ref int startFired, ref int startFrames, ref float startSlipMax)
        {
            // "目立つ=54/81コマ" / "向き 前36/左見11/右見34/背走0/横0コマ" / "発進=2本 11コマ slip=2.03〜4.66"
            var m = System.Text.RegularExpressions.Regex.Match(acc, @"目立つ=(\d+)/(\d+)コマ");
            if (m.Success) { clamp += int.Parse(m.Groups[1].Value); moveFrames += int.Parse(m.Groups[2].Value); }
            m = System.Text.RegularExpressions.Regex.Match(acc, @"前(\d+)/左見(\d+)/右見(\d+)/背走(\d+)/横(\d+)コマ");
            if (m.Success) for (int i = 0; i < 5; i++) facing[i] += int.Parse(m.Groups[i + 1].Value);
            m = System.Text.RegularExpressions.Regex.Match(acc, @"発進=(\d+)本 (\d+)コマ slip=([\d.]+)〜([\d.]+)");
            if (m.Success)
            {
                startFired += int.Parse(m.Groups[1].Value);
                startFrames += int.Parse(m.Groups[2].Value);
                startSlipMax = Mathf.Max(startSlipMax, float.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
            }
        }

        private static float ContactOf(PlaybackPlay play)
        {
            if (play == null || play.Ball.Count == 0) return 0f;
            var head = play.Ball[0];
            bool isPitch = head is PitchSegment
                || (head is EndpointBallSegment eb
                    && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Pitch);
            return isPitch ? (float)head.T1 : 0f;
        }

        private static Texture2D Grab(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        private static void WriteStrip(List<Texture2D> frames, string name)
        {
            if (frames.Count == 0) return;
            int fw = frames[0].width, fh = frames[0].height;
            var sheet = new Texture2D(frames.Count * fw, fh, TextureFormat.RGBA32, false);
            try
            {
                for (int i = 0; i < frames.Count; i++) sheet.SetPixels32(i * fw, 0, fw, fh, frames[i].GetPixels32());
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(Dir, name + ".png"), sheet.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(sheet);
            }
        }
    }
}
#endif

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】送球の**体の向き**の確認撮影（issue #505 ②）。
    ///
    /// 試合の守備カメラは引きの構図（frameH 29〜65m）で野手が十数pxにしかならず、
    /// 「どっちを向いて投げ始めたか」は絵から読めない。ここでは投げ手を
    /// **送球線の真後ろ**から寄って撮る＝正しく的を向いていれば背中が見える。
    /// ずれていれば横を向いた分だけ体が回って見える（判定が一目で済む構図）。
    ///
    /// ⚠ 構図は「縦に収めたい実寸[m]」で指定する（design-18 §3.5.1・画角で指定しない）。
    /// </summary>
    public static class DuelThrowFacingShot
    {
        private const string Dir = "out/duel-stage/throwfacing";

        private const int W = 560;
        private const int H = 560;

        /// <summary>1段に並べるコマ数（2段＝8コマ。横一列だと1コマが潰れて向きが読めない）。</summary>
        private const int Cols = 4;

        /// <summary>1本の送球で並べるコマ数（12fps のポーズ格子に合わせて拾う）。</summary>
        private const int Frames = 8;

        /// <summary>
        /// 投げ始めの手前から見せる長さ[s]（＝「投げる前は何を向いていたか」を写す）。
        /// ⚠ **リリースからの逆算なので、収録のリリース位置（0.68〜1.36s）より長く取る**。
        /// 0.34s にしていたら窓が丸ごと投げ始めより後ろに入り、肝心の切り替わりが1コマも写らなかった
        /// （2026-08-06 実害）。
        /// </summary>
        private const float LeadSeconds = 0.9f;

        /// <summary>リリース後に見せる長さ[s]。</summary>
        private const float TailSeconds = 0.25f;

        /// <summary>寄りの深さ＝縦に収める実寸[m]（背丈1.75mが枠の半分強に写る）。</summary>
        private const float FrameHeightM = 3.2f;

        /// <summary>ゴロ 6-3 ／ 右中間の中継バックホーム ／ 6-4-3 併殺（送球が出る3本）。</summary>
        private static readonly int[] PlayIndices = { 2, 5, 4 };

        [UnityEditor.MenuItem("KokoSim/Duel/#505 送球の体の向きを撮る")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);
            var throws = new StringBuilder();

            foreach (var idx in PlayIndices)
            {
                var play = PlaybackSamples.All[idx];
                var plan = ThrowsOf(play);
                if (plan.Count == 0) continue;

                var rig = new DuelStageRig();
                try
                {
                    if (!rig.Available)
                    {
                        Debug.LogError("[#505Shot] DuelStageRig が組めない（素体/クリップ未導入）");
                        return;
                    }
                    rig.EnsureSize(W, H);
                    float contact = ContactOf(play);
                    rig.BeginFieldPlay(play, contact);

                    // 送球ごとに「拾いたい時刻」を並べておく（1回の通しで全部撮る）
                    var shots = new List<KeyValuePair<float, int>>();   // 時刻 → 送球の添字
                    for (int k = 0; k < plan.Count; k++)
                    {
                        float t0 = plan[k].TRelease - LeadSeconds;
                        float t1 = plan[k].TRelease + TailSeconds;
                        for (int f = 0; f < Frames; f++)
                            shots.Add(new KeyValuePair<float, int>(
                                t0 + (t1 - t0) * f / (Frames - 1), k));
                    }

                    var sheets = new Dictionary<int, List<Texture2D>>();
                    int next = 0;
                    float poseStep = 1f / DuelStageRig.PoseFps;
                    // **実ゲームと同じ刻みで通す**（飛び石で進めると向きのレート制限も足の運びも測れない）
                    for (float t = contact; next < shots.Count && t <= play.Dur + 0.5; t += poseStep)
                    {
                        if (t > contact) rig.Tick(poseStep);
                        rig.SetFieldTime(t);
                        while (next < shots.Count && t + 1e-4f >= shots[next].Key)
                        {
                            int k = shots[next].Value;
                            AimAtThrower(rig, play, plan[k], t);
                            rig.RenderNow();
                            if (!sheets.ContainsKey(k)) sheets[k] = new List<Texture2D>();
                            sheets[k].Add(Grab(rig.Target));
                            next++;
                        }
                    }
                    rig.ClearPreviewFrame();

                    foreach (var kv in sheets)
                    {
                        WriteSheet(kv.Value, Label(idx) + "-" + kv.Key + "-"
                            + plan[kv.Key].From + "to" + plan[kv.Key].To);
                        foreach (var f in kv.Value) Object.DestroyImmediate(f);
                    }

                    throws.AppendLine("# " + Label(idx));
                    throws.Append(rig.FieldThrowDiagForCapture());
                }
                finally
                {
                    rig.Dispose();
                }
            }

            File.WriteAllText(Path.Combine(Dir, "throws.tsv"), throws.ToString());
            Debug.Log("[#505Shot] DONE -> " + Dir + "\n" + throws);
        }

        /// <summary>
        /// 投げ手を**送球線の真後ろ・斜め上**から狙う。的を向いていれば背中が見える構図。
        /// 位置は engine の真値（rig が野手を置いているのと同じ物）から採る。
        /// </summary>
        private static void AimAtThrower(DuelStageRig rig, PlaybackPlay play, ThrowPlan p, float t)
        {
            var fp = PlaybackEvaluator.FielderAt(play, p.From, Mathf.Min(t, p.TRelease)).Pos;
            var here = new Vector3((float)fp.X, 0f, (float)fp.Y);
            var dir = new Vector3(p.Target.x - here.x, 0f, p.Target.z - here.z);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            dir.Normalize();
            // 真後ろから 4.2m 引いて 2.4m の高さ＝背中と足元の向きが同時に読める
            rig.FramePreview(here - dir * 4.2f + Vector3.up * 2.4f,
                here + Vector3.up * 1.0f, FrameHeightM);
        }

        private struct ThrowPlan
        {
            public FieldPosition From, To;
            public float TRelease;
            public Vector3 Target;
        }

        /// <summary>
        /// このプレーの送球を洗い出す（<c>DuelStageRig.BuildFieldThrowPlan</c> と同じ読み方。
        /// 撮影側は「誰がいつどこへ投げるか」だけ判ればよいのでアンカーは引かない）。
        /// </summary>
        private static List<ThrowPlan> ThrowsOf(PlaybackPlay play)
        {
            var list = new List<ThrowPlan>();
            if (play?.Ball == null) return list;
            foreach (var seg in play.Ball)
            {
                var eb = seg as EndpointBallSegment;
                bool isThrow = eb != null ? eb.Kind == BallSegmentKind.Throw : seg is ThrowSegment;
                if (!isThrow) continue;
                var from = seg.At(0.0);
                var to = seg.At(seg.T1 - seg.T0);
                // ⚠ 役は engine が持っているとは限らない（PlaybackSamples は FromRole を入れない）。
                //    rig の BuildFieldThrowPlan と**同じ**フォールバック＝始点に一番近い野手にする。
                //    ここを外すと1本も撮れない（2026-08-06 実害: 空のシートが出た）
                list.Add(new ThrowPlan
                {
                    From = seg.FromRole ?? Nearest(play, (float)seg.T0, from.X, from.Y),
                    To = seg.ToRole ?? Nearest(play, (float)seg.T1, to.X, to.Y),
                    TRelease = (float)seg.T0,
                    Target = new Vector3((float)to.X, (float)to.H, (float)to.Y),
                });
            }
            return list;
        }

        /// <summary>その点に一番近い野手（rig の <c>NearestFielder</c> と同じ）。</summary>
        private static FieldPosition Nearest(PlaybackPlay play, float t, double x, double y)
        {
            var all = new[]
            {
                FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase,
                FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop,
                FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
            };
            var best = FieldPosition.Pitcher;
            float bestD = float.MaxValue;
            foreach (var pos in all)
            {
                var p = PlaybackEvaluator.FielderAt(play, pos, t).Pos;
                float d = new Vector2((float)(p.X - x), (float)(p.Y - y)).magnitude;
                if (d < bestD) { bestD = d; best = pos; }
            }
            return best;
        }

        /// <summary>打球の瞬間＝先頭の投球セグメントの終わり（DuelFieldPlayShot と同じ定義）。</summary>
        private static float ContactOf(PlaybackPlay play)
        {
            if (play == null || play.Ball.Count == 0) return 0f;
            var head = play.Ball[0];
            bool isPitch = head is PitchSegment
                || (head is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Pitch);
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

        private static void WriteSheet(List<Texture2D> frames, string name)
        {
            if (frames.Count == 0) return;
            int cols = Mathf.Min(Cols, frames.Count);
            int rows = Mathf.CeilToInt(frames.Count / (float)cols);
            int fw = frames[0].width, fh = frames[0].height;
            var sheet = new Texture2D(cols * fw, rows * fh, TextureFormat.RGBA32, false);
            try
            {
                // Texture2D は左下原点。コマは左上から時系列に並べたいので行を反転する
                for (int i = 0; i < frames.Count; i++)
                    sheet.SetPixels32((i % cols) * fw, (rows - 1 - i / cols) * fh, fw, fh,
                        frames[i].GetPixels32());
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(Dir, name + ".png"), sheet.EncodeToPNG());
                Debug.Log("[#505Shot] wrote " + name + " (" + frames.Count + " コマ)");
            }
            finally
            {
                Object.DestroyImmediate(sheet);
            }
        }

        private static string Label(int idx)
        {
            switch (idx)
            {
                case 2: return "grd";
                case 5: return "xbh";
                case 4: return "dp";
                default: return "p" + idx;
            }
        }
    }
}
#endif

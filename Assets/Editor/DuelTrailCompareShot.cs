#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】守備ビューの打球残像の**弱め方の見比べ**（issue #505 ①）。
    ///
    /// 残像の太さ・濃さ・尾の長さは「絵で決める値」なので、段を振って並べて監督に選んでもらう
    /// （design-18 §4.5-④ で対決ビュー用に 0.045m/0.45/0.28s を決めたときと同じ作法）。
    /// 1枚のシートで **行＝案・列＝時刻**。同じプレー・同じ時刻の絵が縦に並ぶので強さだけを比べられる。
    /// </summary>
    public static class DuelTrailCompareShot
    {
        private const string Dir = "out/duel-stage/trailcompare";

        private const int W = 640;
        private const int H = 360;

        /// <summary>1案あたりに並べる時刻の数。</summary>
        private const int Cols = 4;

        private struct Variant
        {
            public string Name;
            public float Seconds, WidthM, Alpha;
            public bool OnThrow;
        }

        /// <summary>
        /// 現行＋弱2段。**送球にも残像を出すか**は太さ・濃さと独立の選択なので、
        /// 弱い案では併せて切ってある（送球は始点も終点も絵に写っており線で補う必要がない）。
        /// </summary>
        private static readonly Variant[] Variants =
        {
            new Variant { Name = "A-genkou", Seconds = 0.45f, WidthM = 0.35f, Alpha = 0.45f, OnThrow = true },
            new Variant { Name = "B-yowame", Seconds = 0.35f, WidthM = 0.22f, Alpha = 0.28f, OnThrow = false },
            new Variant { Name = "C-saiyowa", Seconds = 0.28f, WidthM = 0.14f, Alpha = 0.18f, OnThrow = false },
        };

        /// <summary>ゴロ 6-3 ／ センターフライ ／ 右中間の長打→中継。</summary>
        private static readonly int[] PlayIndices = { 2, 1, 5 };

        [UnityEditor.MenuItem("KokoSim/Duel/#505 残像の弱め方を見比べる")]
        public static void Run()
        {
            Directory.CreateDirectory(Dir);

            // ⚠ static のノブを触るので、必ず元へ戻す（撮影の副作用を実ゲームへ残さない）
            float s0 = DuelStageRig.FieldBattedTrailSeconds;
            float w0 = DuelStageRig.FieldBattedTrailWidthM;
            float a0 = DuelStageRig.FieldBattedTrailAlpha;
            bool t0 = DuelStageRig.FieldTrailOnThrow;
            try
            {
                foreach (var idx in PlayIndices)
                {
                    var rows = new List<List<Texture2D>>();
                    foreach (var v in Variants)
                    {
                        DuelStageRig.FieldBattedTrailSeconds = v.Seconds;
                        DuelStageRig.FieldBattedTrailWidthM = v.WidthM;
                        DuelStageRig.FieldBattedTrailAlpha = v.Alpha;
                        DuelStageRig.FieldTrailOnThrow = v.OnThrow;
                        rows.Add(ShootPlay(PlaybackSamples.All[idx]));
                    }
                    WriteSheet(rows, Label(idx));
                    foreach (var r in rows) foreach (var f in r) Object.DestroyImmediate(f);
                }
            }
            finally
            {
                DuelStageRig.FieldBattedTrailSeconds = s0;
                DuelStageRig.FieldBattedTrailWidthM = w0;
                DuelStageRig.FieldBattedTrailAlpha = a0;
                DuelStageRig.FieldTrailOnThrow = t0;
            }
            Debug.Log("[#505Trail] DONE -> " + Dir);
        }

        private static List<Texture2D> ShootPlay(PlaybackPlay play)
        {
            var frames = new List<Texture2D>();
            var rig = new DuelStageRig();
            try
            {
                if (!rig.Available)
                {
                    Debug.LogError("[#505Trail] DuelStageRig が組めない（素体/クリップ未導入）");
                    return frames;
                }
                rig.EnsureSize(W, H);
                float contact = ContactOf(play);
                rig.BeginFieldPlay(play, contact);

                // **実ゲームと同じ刻みで通す**（飛び石で進めると残像そのものが積まれない）
                float shotStep = ((float)play.Dur - contact) / Cols;
                float poseStep = 1f / DuelStageRig.PoseFps;
                int shot = 0;
                for (float t = contact; shot < Cols; t += poseStep)
                {
                    if (t > contact) rig.Tick(poseStep);
                    rig.SetFieldTime(t);
                    // 頭（contact ちょうど）は残像が1点しかないので、1コマぶん後ろから拾う
                    if (t + 1e-4f < contact + (shot + 0.6f) * shotStep) continue;
                    rig.RenderNow();
                    frames.Add(Grab(rig.Target));
                    shot++;
                }
            }
            finally
            {
                rig.Dispose();
            }
            return frames;
        }

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

        /// <summary>行＝案・列＝時刻で1枚に並べる（同じ時刻が縦に揃う＝強さだけを比べられる）。</summary>
        private static void WriteSheet(List<List<Texture2D>> rows, string name)
        {
            if (rows.Count == 0 || rows[0].Count == 0) return;
            int cols = rows[0].Count;
            int fw = rows[0][0].width, fh = rows[0][0].height;
            var sheet = new Texture2D(cols * fw, rows.Count * fh, TextureFormat.RGBA32, false);
            try
            {
                for (int r = 0; r < rows.Count; r++)
                    for (int c = 0; c < rows[r].Count; c++)
                        // Texture2D は左下原点。案は上から A/B/C の順に並べたいので行を反転する
                        sheet.SetPixels32(c * fw, (rows.Count - 1 - r) * fh, fw, fh,
                            rows[r][c].GetPixels32());
                sheet.Apply();
                File.WriteAllBytes(Path.Combine(Dir, name + ".png"), sheet.EncodeToPNG());
                Debug.Log("[#505Trail] wrote " + name);
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
                case 1: return "fly";
                case 2: return "grd";
                case 5: return "xbh";
                default: return "p" + idx;
            }
        }
    }
}
#endif

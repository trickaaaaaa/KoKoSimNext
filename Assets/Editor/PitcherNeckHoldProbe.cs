#if UNITY_EDITOR
using System.IO;
using System.Text;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】投球中の投手の首の実測と撮影（issue #973）。
    /// セット→投球→リリース→フォロースルーを12fpsのコマ列で流し、首・頭の turn／tilt／nod を毎コマ出す。
    /// </summary>
    public static class PitcherNeckHoldProbe
    {
        private const string Dir = "out/pitcher-neck-973";
        private const string ShotDir = "screenshots/pitcher-neck-973";

        [UnityEditor.MenuItem("KokoSim/Duel/#973 投球中の首を測る（右投手）")]
        public static void RunRight() => Run(false);

        [UnityEditor.MenuItem("KokoSim/Duel/#973 投球中の首を測る（左投手）")]
        public static void RunLeft() => Run(true);

        [UnityEditor.MenuItem("KokoSim/Duel/#973 投げ終わり→次のセットを測る（右投手）")]
        public static void RunReturnToSet()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(ShotDir);
            var rig = new DuelStageRig();
            if (!rig.Available) { Debug.LogError("[#973Probe] DuelStageRig が組めない"); return; }
            rig.EnsureSize(960, 540);
            rig.SetHandedness(false, false);
            float dt = 1f / 60f;
            var log = new StringBuilder("phase\tt\tglove\thead\n");
            for (int i = 0; i < 30; i++) rig.Tick(dt);
            rig.OnPitchWindup();
            float t = 0f;
            bool released = false;
            int shot = 0;
            // 最後の1球（nextReleaseIn=0）: フォロースルーを末尾まで→セット姿勢で止まる
            while (t < DuelStageRig.WindupSeconds + 4.0f)
            {
                if (!released && t >= DuelStageRig.WindupSeconds - 0.001f)
                {
                    released = true;
                    rig.OnPitchReleased(0f, DuelPitchOutcome.CalledStrike);
                }
                rig.Tick(dt);
                t += dt;
                if (released)
                    log.Append("post\t").Append(t.ToString("F3")).Append('\t').Append(GloveW(rig)).Append('\t')
                       .Append(Diag(rig)).Append('\n');
                if (released && t > DuelStageRig.WindupSeconds + 1.6f && shot < 6 && ((int)(t * 60f)) % 10 == 0)
                    Shoot(rig, "ret-" + (shot++) + "-t" + t.ToString("F2"));
            }
            // 新しい打席の頭（ResetForNewPlateAppearance）でもセットへ寄せる
            rig.ResetForNewPlateAppearance();
            for (int i = 0; i < 30; i++)
            {
                rig.Tick(dt);
                log.Append("reset\t").Append((i * dt).ToString("F3")).Append('\t').Append(GloveW(rig)).Append('\t')
                   .Append(Diag(rig)).Append('\n');
            }
            File.WriteAllText(Path.Combine(Dir, "return-to-set.tsv"), log.ToString());
            Debug.Log("[#973Probe] return-to-set done");
            rig.Dispose();
        }

        private static string GloveW(DuelStageRig rig)
        {
            var f = typeof(DuelStageRig).GetField("_setGloveW",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var a = typeof(DuelStageRig).GetField("_setGloveApplied",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return "gw=" + ((float)f.GetValue(rig)).ToString("F2") + " ga=" + ((float)a.GetValue(rig)).ToString("F2");
        }

        private static void Run(bool left)
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(ShotDir);
            string tag = left ? "L" : "R";
            var rig = new DuelStageRig();
            if (!rig.Available) { Debug.LogError("[#973Probe] DuelStageRig が組めない"); return; }
            rig.EnsureSize(960, 540);
            rig.SetHandedness(false, left);
            float step = 1f / DuelStageRig.PoseFps;
            var log = new StringBuilder("phase\tt\t" + "diag\n");
            for (int i = 0; i < 12; i++) rig.Tick(step);
            log.Append("set\t0\t").Append(Diag(rig)).Append('\n');
            Shoot(rig, tag + "-0-set");

            rig.OnPitchWindup();
            float t = 0f;
            float release = DuelStageRig.WindupSeconds;
            float end = release + 0.9f;
            bool released = false;
            int frame = 0;
            var shotAt = new[] { 0.0f, 0.4f, 0.8f, 1.1f, 1.3f, 1.45f, 1.6f, 1.9f };
            int shotIdx = 0;
            while (t < end)
            {
                if (!released && t >= release - 0.001f)
                {
                    released = true;
                    rig.OnPitchReleased(4f, DuelPitchOutcome.CalledStrike);
                }
                rig.Tick(step);
                t += step;
                frame++;
                log.Append(released ? "post\t" : "pre\t").Append(t.ToString("F3")).Append('\t')
                   .Append(Diag(rig)).Append('\n');
                if (shotIdx < shotAt.Length && t >= shotAt[shotIdx] - 0.001f)
                {
                    Shoot(rig, tag + "-" + (shotIdx + 1) + "-t" + t.ToString("F2"));
                    shotIdx++;
                }
            }
            File.WriteAllText(Path.Combine(Dir, "neck-" + tag + ".tsv"), log.ToString());
            Debug.Log("[#973Probe] " + tag + " done frames=" + frame);
            rig.Dispose();
        }

        private static string Diag(DuelStageRig rig)
        {
            var m = typeof(DuelStageRig).GetMethod("SetHeadDiagForCapture",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return m != null ? (string)m.Invoke(rig, null) : "no diag";
        }

        private static void Shoot(DuelStageRig rig, string file)
        {
            var look = new Vector3(0f, 1.55f, 17.6f);
            rig.FramePreview(new Vector3(0.6f, 1.75f, 13.8f), look, 2.2f);
            Save(rig, file + "-front");
            rig.FramePreview(new Vector3(-4.2f, 1.6f, 17.6f), look, 2.4f);
            Save(rig, file + "-side");
            rig.FramePreview(new Vector3(0.3f, 2.0f, 24.5f), new Vector3(0f, 1.4f, 17.6f), 2.6f);
            Save(rig, file + "-back");
            rig.ClearPreviewFrame();
        }

        private static void Save(DuelStageRig rig, string file)
        {
            rig.RenderNow();
            var rt = rig.Target;
            if (rt == null) return;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(ShotDir, file + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
#endif

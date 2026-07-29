#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】PA進行に伴う装備消失バグの再現プローブ。
    /// 打席を数回進め、各打席で (a) 打者の頭のgear-head子の実在・位置 (b) 対決ビューRTのPNG を記録する
    /// （2026-07-28: 左打・斉藤の打席でヘルメットが素頭に見えた事象の切り分け）。原因確定後は削除してよい。
    /// </summary>
    public sealed class DuelGearPaProbeShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int Paces = 5;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[PaProbe] controller not found"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);
            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            for (int k = 0; k < Paces; k++)
            {
                ctrl.AdvanceForCapture();
                ctrl.RestartCurrentPaForCapture();
                for (int i = 0; i < 24; i++) yield return null;
                Probe(k);
                SaveTarget(k);
                yield return null;
            }
            Debug.Log("[PaProbe] DONE");
        }

        private static void Probe(int k)
        {
            int layer = LayerMask.NameToLayer("DuelStage");
            var sb = new StringBuilder();
            sb.Append("[PaProbe] pa=").Append(k);
            Animator batter = null;
            foreach (var a in Resources.FindObjectsOfTypeAll<Animator>())
            {
                if (a == null || !a.gameObject.activeInHierarchy || a.gameObject.layer != layer) continue;
                if (a.gameObject.name.Contains("Batter") || a.gameObject.name.Contains("Batting")) { batter = a; break; }
            }
            if (batter == null) { Debug.Log(sb.Append(" batter=NONE").ToString()); return; }
            sb.Append(" batter=").Append(batter.gameObject.name);
            var head = batter.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) { Debug.Log(sb.Append(" head=NONE").ToString()); return; }
            int parts = 0;
            foreach (Transform ch in head)
            {
                if (ch.name != "gear-head") continue;
                foreach (Transform g in ch)
                {
                    parts++;
                    float dist = Vector3.Distance(g.position, head.position);
                    sb.Append("  ").Append(g.name).Append(" d=").Append(dist.ToString("F2"));
                }
            }
            sb.Append("  parts=").Append(parts);
            Debug.Log(sb.ToString());
        }

        private static void SaveTarget(int k)
        {
            var live = DuelStageRig.Live;
            if (live == null || live.Target == null) { Debug.Log("[PaProbe] no rig target"); return; }
            var rt = live.Target;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, "probe-pa" + k + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }

        private static MatchLiveController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
            }
            return null;
        }
    }
}
#endif

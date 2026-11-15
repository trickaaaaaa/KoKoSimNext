#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Players
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #1027: 選手詳細の成長推移を撮る。
    /// 3年生に入学からの月ごとの記録を合成で入れて3タブを撮り、記録が1点しかない1年生も撮る
    /// （記録の取り方そのものは engine の AbilityHistoryTests で固定してある＝ここは見た目だけ）。
    /// </summary>
    public sealed class Issue1027Shooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 1900;
        public static string Dir = "screenshots/issue-1027";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (int i = 0; i < 4; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var active = RosterService.Active.ToList();
            var senior = active.First(p => p.Grade == 3);
            var fresh = active.First(p => p.Grade == 1);
            Synthesize(senior);

            var list = Activate("PlayerList");
            yield return null;
            var detail = list.GetComponent<PlayerDetailController>();
            var cal = new SeasonCalendar();

            PlayerSelection.Index = IndexOf(senior);
            detail.RenderForSelection();
            string[] tabs = { "growth-tab-batting", "growth-tab-pitching", "growth-tab-defense" };
            var root = list.GetComponent<UIDocument>().rootVisualElement;
            for (int t = 0; t < tabs.Length; t++)
            {
                using (var e = ClickEvent.GetPooled()) { e.target = root.Q<Label>(tabs[t]); root.Q<Label>(tabs[t]).SendEvent(e); }
                yield return Capture(list, "v3-senior-tab" + t);
            }

            using (var e = ClickEvent.GetPooled()) { e.target = root.Q<Label>(tabs[0]); root.Q<Label>(tabs[0]).SendEvent(e); }
            PlayerSelection.Index = IndexOf(fresh);
            detail.RenderForSelection();
            yield return Capture(list, "v3-fresh");

            Debug.Log("[1027] DONE");
        }

        private static int IndexOf(Engine.Season.DevelopingPlayer p)
        {
            // 選手詳細の Index は RosterService.Roster の並び。
            var r = RosterService.Roster;
            for (int i = 0; i < r.Count; i++) if (ReferenceEquals(r[i], p)) return i;
            return 0;
        }

        /// <summary>入学（1年4月）から今（3年4月）まで、月の頭ごとに今の値へ向かって伸びる記録を作る。</summary>
        private static void Synthesize(Engine.Season.DevelopingPlayer p)
        {
            var cal = new SeasonCalendar();
            var now = p.Grade;
            p.History.Clear();
            var steps = new System.Collections.Generic.List<(int g, int w)>();
            for (int g = 1; g < now; g++)
                for (int w = 0; w < cal.WeeksPerYear; w++)
                    if (AbilityHistory.IsSampleWeek(w, cal)) steps.Add((g, w));
            for (int i = 0; i < steps.Count; i++)
            {
                var t = (float)i / steps.Count;
                var levels = new int[AbilityKinds.All.Length];
                foreach (var k in AbilityKinds.All)
                {
                    var gain = 8 + ((int)k * 5) % 17;        // 能力ごとに伸び幅を変える
                    var wobble = ((i * 7 + (int)k * 3) % 5) - 2;   // 冬・夏の伸びの凸凹
                    var shaped = Mathf.Pow(t, 0.8f + ((int)k % 3) * 0.3f);
                    levels[(int)k] = Mathf.Clamp(Mathf.RoundToInt(p.Level(k) - gain * (1 - shaped) + (i > 0 ? wobble * 0.5f : 0)), 1, 99);
                }
                p.History.Add(new AbilitySample(steps[i].g, steps[i].w, levels));
            }
        }

        private IEnumerator Capture(MonoBehaviour ctrl, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;
            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            try
            {
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[1027] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt); Destroy(tex);
            }
        }

        private static MonoBehaviour Activate(string screen)
        {
            MonoBehaviour found = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                var on = d.gameObject.name == screen;
                d.gameObject.SetActive(on);
                if (!on) continue;
                foreach (var mb in d.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.GetType().Name.EndsWith("Controller")) found = mb;
            }
            return found;
        }
    }
}
#endif

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】ホーム画面が週を進めるほど縦に伸びる不具合の実測プローブ。
    ///
    /// <para><b>実際の週送り（「今週を進める」ボタン）を押して</b>測る＝イベント・ノベル・フィードを
    /// 含む本物の経路を通す（GameClock.Advance を直接呼ぶと、そこで増える物が出ないので再現しない）。
    /// 毎週、トップバー／ヒーロー／本文域の高さと、画面からはみ出した要素の数を記録する。使い捨て。</para>
    /// </summary>
    public sealed class HomeGrowthProbe : MonoBehaviour
    {
        private const int Weeks = 24;
        private const string Dir = "screenshots/home-growth";
        private const int W = 1600;
        private const int ShotH = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory("out");
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            sb.AppendLine("i\tweek\tpanelH\ttopbarH\ttopbarY\theroH\tdashH\trootH\toffscreen\tworst");

            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var doc = Open("HomeDashboard");
            if (doc == null) { Debug.LogError("[HG] HomeDashboard が無い"); yield break; }
            yield return null; yield return null;

            for (var step = 0; step <= Weeks; step++)
            {
                var root = doc.rootVisualElement;
                var panelH = root.layout.height;
                var page = root.Q<VisualElement>(className: "root") ?? root;
                var top = FirstChild(page, 0);
                var hero = root.Q<VisualElement>("home-hero");
                var dash = root.Q<VisualElement>("home-dash");

                var off = 0;
                var worst = "";
                var worstY = 0f;
                foreach (var e in Walk(root))
                {
                    var wb = e.worldBound;
                    if (float.IsNaN(wb.height) || wb.height <= 0.5f) continue;
                    if (e.resolvedStyle.display == DisplayStyle.None) continue;
                    if (wb.yMin >= -0.5f && wb.yMax <= panelH + 0.5f) continue;
                    off++;
                    var over = Mathf.Max(-wb.yMin, wb.yMax - panelH);
                    if (over > worstY) { worstY = over; worst = Name(e) + " " + wb.yMin.ToString("F0") + ".." + wb.yMax.ToString("F0"); }
                }

                if (step == 0 || step == 8 || step == 20) yield return Capture(doc, "step" + step.ToString("00"));

                sb.Append(step).Append('\t').Append(GameClock.Week).Append('\t').Append(panelH.ToString("F0")).Append('\t')
                  .Append(H(top)).Append('\t').Append(Y(top)).Append('\t')
                  .Append(H(hero)).Append('\t').Append(H(dash)).Append('\t').Append(H(page)).Append('\t')
                  .Append(off).Append('\t').Append(worst).AppendLine();

                // 週を進める（モーダル・ノベルが立っていたら閉じてから）。
                // ⚠ Button へ ClickEvent を投げても clicked は走らない（Clickable は PointerUp で発火する）。
                // 実プレイと同じ入口をそのまま叩く＝コントローラの OnAdvance を反射で呼ぶ。
                yield return Dismiss(root);
                var ctrl = FindFirstObjectByType<HomeDashboardController>();
                if (ctrl == null) { Debug.LogError("[HG] HomeDashboardController が無い"); break; }
                var m = typeof(HomeDashboardController).GetMethod(
                    "OnAdvance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (m == null) { Debug.LogError("[HG] OnAdvance が無い"); break; }
                m.Invoke(ctrl, null);
                for (var f = 0; f < 4; f++) { yield return null; yield return new WaitForEndOfFrame(); }
            }

            // 他画面の回帰確認（.topbar-slot / .card__scroll は共通USS＝全画面に効く）
            foreach (var screen in new[] { "PlayerList", "TrainingPlan", "RecordsScreen", "ManagementScreen", "MemberSetting" })
            {
                var d2 = Open(screen);
                if (d2 == null) continue;
                yield return null; yield return null;
                yield return Capture(d2, "screen-" + screen);
            }

            File.WriteAllText("out/home-growth.tsv", sb.ToString());
            Debug.Log("[HG] DONE out/home-growth.tsv\n" + sb);
        }

        private IEnumerator Capture(UIDocument doc, string file)
        {
            var ps = doc.panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, ShotH, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, ShotH);
            ps.targetTexture = rt;
            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(W, ShotH, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, ShotH), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        /// <summary>立っているモーダル・ノベルを閉じる（実プレイと同じ操作＝クリック）。</summary>
        private static IEnumerator Dismiss(VisualElement root)
        {
            for (var guard = 0; guard < 40; guard++)
            {
                var novel = root.Q<VisualElement>("ev-novel");
                if (novel != null && novel.resolvedStyle.display != DisplayStyle.None)
                {
                    using (var ev = PointerDownEvent.GetPooled()) { ev.target = novel; novel.SendEvent(ev); }
                    yield return null;
                    continue;
                }
                var btn = FirstVisibleButton(root, "nt-ok", "tm-result-ok", "tm-dialog-yes");
                if (btn == null) yield break;
                using (var ev = ClickEvent.GetPooled()) { ev.target = btn; btn.SendEvent(ev); }
                yield return null;
            }
        }

        private static Button FirstVisibleButton(VisualElement root, params string[] names)
        {
            foreach (var n in names)
            {
                var b = root.Q<Button>(n);
                if (b == null) continue;
                var p = b.parent;
                var visible = true;
                while (p != null) { if (p.resolvedStyle.display == DisplayStyle.None) { visible = false; break; } p = p.parent; }
                if (visible) return b;
            }
            return null;
        }

        private static VisualElement FirstChild(VisualElement e, int index)
        {
            var i = 0;
            foreach (var c in e.Children()) { if (i++ == index) return c; }
            return null;
        }

        private static string H(VisualElement e) => e == null ? "-" : e.layout.height.ToString("F0");
        private static string Y(VisualElement e) => e == null ? "-" : e.worldBound.yMin.ToString("F0");

        private static IEnumerable<VisualElement> Walk(VisualElement e)
        {
            yield return e;
            foreach (var c in e.Children())
                foreach (var n in Walk(c))
                    yield return n;
        }

        private static string Name(VisualElement e)
        {
            var n = string.IsNullOrEmpty(e.name) ? e.GetType().Name : e.name;
            foreach (var c in e.GetClasses()) { n += "." + c; break; }
            return n;
        }

        private static UIDocument Open(string screen)
        {
            UIDocument target = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) target = d;
                d.gameObject.SetActive(false);
            }
            if (target != null) target.gameObject.SetActive(true);
            return target;
        }
    }
}
#endif

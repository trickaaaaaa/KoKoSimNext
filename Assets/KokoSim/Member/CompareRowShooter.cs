#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Member
{
    /// <summary>
    /// 【開発用・エディタ限定】選手比較パネル（issue #4: バーの起点揃え）のスクショ確認用。使い捨て。
    /// メンバー設定・スタメン設定の両画面で、A/B 2人選択時と片側のみ（「—」表示）を撮る。
    /// 選択状態は State の private フィールドへ直接入れる（クリック操作は背番号の交換など副作用を伴うため）。
    /// </summary>
    public sealed class CompareRowShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue-4";

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // ── メンバー設定 ──
            var member = Activate("MemberSetting");
            if (member == null) { Debug.LogError("[CmpShooter] MemberSetting が見つからない"); yield break; }
            yield return null;

            Select(member, 0, 1, 0);
            yield return Capture(member, "member-compare-bat");

            // 守備適性ダイヤは比較スクロールの下端にあり、既定では窓の外に落ちている（issue #452 の突き合わせ）。
            // 「在るのに見えない」ことを絵で示すため、下端まで送った状態も撮る。
            ScrollCompareToBottom(member);
            yield return Capture(member, "member-compare-apt");
            ScrollCompareToTop(member);

            Select(member, 0, 1, 1);
            yield return Capture(member, "member-compare-def");

            Select(member, 0, -1, 0);
            yield return Capture(member, "member-compare-single");

            // ── スタメン設定 ──
            var lineup = Activate("LineupSetting");
            if (lineup == null) { Debug.LogError("[CmpShooter] LineupSetting が見つからない"); yield break; }
            yield return null;

            Select(lineup, 0, 1, 0);
            yield return Capture(lineup, "lineup-compare-bat");

            ScrollCompareToBottom(lineup);
            yield return Capture(lineup, "lineup-compare-apt");

            Debug.Log("[CmpShooter] DONE");
        }

        // 比較パネルのスクロールを下端／上端へ送る。クラス名は画面ごとに違う（ms-cmp-scroll / lu-cmp-scroll）ので
        // 「cmp-scroll で終わる」で拾う。scrollOffset は verticalScroller.highValue が上限。
        private static void ScrollCompareToBottom(MonoBehaviour ctrl) => ScrollCompare(ctrl, true);
        private static void ScrollCompareToTop(MonoBehaviour ctrl) => ScrollCompare(ctrl, false);

        private static void ScrollCompare(MonoBehaviour ctrl, bool bottom)
        {
            var root = ctrl.GetComponent<UIDocument>()?.rootVisualElement;
            if (root == null) return;
            foreach (var sv in root.Query<ScrollView>().ToList())
            {
                var isCompare = false;
                foreach (var c in sv.GetClasses()) if (c.EndsWith("cmp-scroll")) isCompare = true;
                if (!isCompare) continue;
                sv.scrollOffset = new Vector2(0, bottom ? sv.verticalScroller.highValue : 0f);
            }
        }

        // 比較の左右（picked=左, hovered=右。hovered=-1 で片側欠損）とタブを差し込んで再描画する。
        private static void Select(MonoBehaviour ctrl, int picked, int hovered, int tab)
        {
            var stateField = ctrl.GetType().GetField("_state", Priv);
            var state = stateField?.GetValue(ctrl);
            if (state == null) return;
            state.GetType().GetField("_picked", Priv)?.SetValue(state, picked);
            state.GetType().GetField("_hovered", Priv)?.SetValue(state, hovered);
            state.GetType().GetField("_tab", Priv)?.SetValue(state, tab);
            ctrl.GetType().GetMethod("Render", Priv)?.Invoke(ctrl, null);
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

            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[CmpShooter] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        // 対象画面だけを表示する（他画面の UIDocument が重なると描画が競合する）。
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

#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Member
{
    /// <summary>
    /// 【開発用・エディタ限定】小型球種チャートのラベル重なり（issue #560）の確認用。使い捨て。
    /// 持ち球の多い投手（背番号10・変化球3種＝チップ4枚）で、
    /// ①メンバー設定の比較カード（小型＝略記になるはず）②選手詳細の大きいチャート（正式名のまま）を撮る。
    /// </summary>
    public sealed class PitchChartLabelShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue-560";
        private const int PitcherIndex = 16;   // 背番号10・変化球3種

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // ── メンバー設定（比較カード＝小型チャート） ──
            var member = Activate("MemberSetting");
            if (member == null) { Debug.LogError("[ChartShooter] MemberSetting が見つからない"); yield break; }
            yield return null;

            // 選択は State の private フィールドへ直接入れる（クリックは背番号交換の副作用を伴う）。
            var state = member.GetType().GetField("_state", Priv)?.GetValue(member);
            state?.GetType().GetField("_picked", Priv)?.SetValue(state, PitcherIndex);   // 比較の左
            state?.GetType().GetField("_hovered", Priv)?.SetValue(state, 1);            // 比較の右
            state?.GetType().GetField("_tab", Priv)?.SetValue(state, 1);   // 投手能力タブ
            member.GetType().GetMethod("Render", Priv)?.Invoke(member, null);
            yield return Capture(member, "member-compact-chart");

            // ── 選手詳細（大きいチャート＝略記にしない） ──
            var list = Activate("PlayerList");
            if (list == null) { Debug.LogError("[ChartShooter] PlayerList が見つからない"); yield break; }
            yield return null;

            KokoSim.Unity.Players.PlayerSelection.Index = PitcherIndex;
            list.GetComponent<KokoSim.Unity.Players.PlayerDetailController>()?.RenderForSelection();
            yield return Capture(list, "detail-full-chart");

            Debug.Log("[ChartShooter] DONE");
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
                Debug.Log("[ChartShooter] wrote " + file);
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

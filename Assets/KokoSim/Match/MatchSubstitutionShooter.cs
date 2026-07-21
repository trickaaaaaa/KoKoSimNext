#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】試合中の選手交代モーダル（設計書09 §6 / issue #22）のスクショ確認用。使い捨て。
    /// 作戦パネル（導線）→守備中（投手交代／守備交代）→攻撃中（代打／代走）→DH未使用時の理由表示、を撮る。
    /// </summary>
    public sealed class MatchSubstitutionShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/match2d-live";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            EnsureOnlyMatchLive();
            yield return null;
            yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[SubShooter] MatchLive/Controller が見つからない"); yield break; }
            yield return null;   // OnEnable で試合生成

            Directory.CreateDirectory(Dir);

            // ① 作戦パネル（モーダルは閉じている＝導線ボタンの見え方）。
            ctrl.AdvanceForCapture();
            yield return Capture(ctrl, "sub-0-controls");

            // ② 守備中（自校=後攻なので初回は守備）。既定タブ＝投手交代。
            for (var i = 0; i < 40 && ctrl.ManagerOnOffenseForCapture; i++) ctrl.AdvanceForCapture();
            ctrl.OpenSubstitutionForCapture();
            yield return Capture(ctrl, "sub-1-defense-pitcher");

            // ③ 守備交代タブ（守備位置を選んで控えから入れる）。
            ctrl.SelectSubstitutionKindForCapture(3);
            yield return Capture(ctrl, "sub-2-defense-fielder");

            // ④ DH解除タブ: このデモは非DH制なので「できない理由」が1行で出る。
            ctrl.SelectSubstitutionKindForCapture(4);
            yield return Capture(ctrl, "sub-3-releasedh-blocked");
            ctrl.CloseSubstitutionForCapture();

            // ⑤ 攻撃中（代打）。自校の攻撃になるまで進める。
            for (var i = 0; i < 40 && !ctrl.ManagerOnOffenseForCapture; i++) ctrl.AdvanceForCapture();
            ctrl.OpenSubstitutionForCapture();
            yield return Capture(ctrl, "sub-4-offense-pinchhit");

            // ⑥ 代走（塁上に走者がいる打席境界まで進めてから）。
            ctrl.CloseSubstitutionForCapture();
            for (var i = 0; i < 80 && !ctrl.ManagerCanPinchRunForCapture; i++) ctrl.AdvanceForCapture();
            ctrl.OpenSubstitutionForCapture();
            ctrl.SelectSubstitutionKindForCapture(1);
            yield return Capture(ctrl, "sub-5-offense-pinchrun");

            Debug.Log("[SubShooter] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.FreezeCurrentAtResult();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

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
                Debug.Log("[SubShooter] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
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

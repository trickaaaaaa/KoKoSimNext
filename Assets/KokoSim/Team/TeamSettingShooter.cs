#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Squad
{
    /// <summary>
    /// 【開発用・エディタ限定】チーム設定画面（設計書18 §3.5.1）の受け入れスクショ。
    /// 初期状態 → 領域を送った状態 → 縦縞ありの3枚を撮り、UI原則7箇条の自己レビューに使う。使い捨て。
    /// </summary>
    public sealed class TeamSettingShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            EnsureOnly("TeamSetting");
            yield return null; yield return null;
            EnsureOnly("TeamSetting");
            var doc = FindDoc("TeamSetting");
            if (doc == null) { Debug.LogError("[TS] TeamSetting が見つからない"); yield break; }
            yield return null; yield return null;

            var ctrl = doc.GetComponent<TeamSettingController>();
            if (ctrl == null) { Debug.LogError("[TS] Controller が無い"); yield break; }
            if (doc.rootVisualElement.Q<VisualElement>("ts-rows") == null)
                Debug.LogError("[TS] ts-rows が UXML に無い（UXMLのパース失敗を疑う）");

            // 初期＝校IDからの自動割り当て（桜丘は白＋黒の縦縞）
            yield return Capture(doc, "i-teamset-0-initial");

            // 縞を外して無地に（縞の有無の切替が効くか）
            ctrl.StepForCapture(5, 1);
            yield return null; yield return null;
            yield return Capture(doc, "i-teamset-1-plain");

            // 領域を送る（アンダーシャツ・ソックス・帽子を別々の色へ＝案Bの自由度が効いているか）。
            // 白ユニのまま撮る＝**図の地に白が沈まないか**の確認も兼ねる
            ctrl.StepForCapture(2, 3);
            ctrl.StepForCapture(3, 6);
            ctrl.StepForCapture(4, 9);
            yield return null; yield return null;
            yield return Capture(doc, "i-teamset-2-picked");

            Debug.Log("[TS] DONE");
        }

        private IEnumerator Capture(UIDocument doc, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ps = doc.panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();

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
                Debug.Log("[TS] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        private static void EnsureOnly(string screen)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == screen);
            }
        }

        private static UIDocument FindDoc(string screen)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) return d;
            }
            return null;
        }
    }
}
#endif

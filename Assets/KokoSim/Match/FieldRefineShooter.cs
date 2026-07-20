#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】盤面リファイン（放射ストライプ／ウォーニングトラック／本塁チョーク／
    /// ダート弧のマウンド中心化）の受け入れスクショ。MatchDetail・MatchLive・MemberSetting を
    /// 1回の Play セッションで撮り切る。使い捨て。確認後に削除してよい。
    /// </summary>
    public sealed class FieldRefineShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/field-refine";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // ── MatchDetail（レターボックス盤面）: 静止盤面・内野プレー・外野フライ(フェンス際) ──
            EnsureOnly("MatchDetail");
            yield return null; yield return null;
            EnsureOnly("MatchDetail");
            var detail = Find<MatchDetailController>("MatchDetail");
            if (detail == null) { Debug.LogError("[FieldRefine] MatchDetail が見つからない"); yield break; }

            var detailDoc = detail.GetComponent<UIDocument>();
            detail.CaptureSeek(0, 0.0);
            yield return Capture(detailDoc, 900, 840, "01-detail-static");
            detail.CaptureSeek(0, 2.3);
            yield return Capture(detailDoc, 900, 840, "02-detail-infield-play");
            detail.CaptureSeek(5, 6.6);
            yield return Capture(detailDoc, 900, 840, "03-detail-deep-fly");

            // ── MatchLive（fillColumn 3カラム全景） ──
            EnsureOnly("MatchLive");
            yield return null; yield return null;
            EnsureOnly("MatchLive");
            var live = Find<MatchLiveController>("MatchLive");
            if (live == null) { Debug.LogError("[FieldRefine] MatchLive が見つからない"); yield break; }
            yield return null; // OnEnable で試合生成
            live.FreezeCurrentAtResult();
            yield return Capture(live.GetComponent<UIDocument>(), 1600, 900, "04-live-3col");

            // ── MemberSetting（球場俯瞰図＝ダート弧修正の確認） ──
            EnsureOnly("MemberSetting");
            yield return null; yield return null;
            EnsureOnly("MemberSetting");
            var member = FindDoc("MemberSetting");
            if (member == null) { Debug.LogError("[FieldRefine] MemberSetting が見つからない"); yield break; }
            yield return null;
            yield return Capture(member, 1600, 900, "05-member-field");

            Debug.Log("[FieldRefine] DONE all shots");
        }

        private IEnumerator Capture(UIDocument doc, int w, int h, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ps = doc.panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(w, h);
            ps.targetTexture = rt;

            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[FieldRefine] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        private static void EnsureOnly(string name)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == name);
            }
        }

        private static T Find<T>(string name) where T : MonoBehaviour
        {
            var doc = FindDoc(name);
            return doc == null ? null : doc.GetComponent<T>();
        }

        private static UIDocument FindDoc(string name)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == name) return d;
            }
            return null;
        }
    }
}
#endif

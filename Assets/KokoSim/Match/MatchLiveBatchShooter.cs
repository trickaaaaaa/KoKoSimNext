#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】試合ライブ進行の受け入れ（設計者Claード条件5）:
    /// 公式戦1試合で「7回に代打を1回入れて最後まで進める」を通し、代打が以降の打席に反映されたことを
    /// ログとスクショで確認する。使い捨て。α後に削除してよい。
    /// </summary>
    public sealed class MatchLiveBatchShooter : MonoBehaviour
    {
        private const int W = 900;
        private const int H = 900;
        private const string Dir = "screenshots/match2d-live";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;   // 自動連続進行(issue #173)を止め、AdvanceForCapture で手動駆動する
            EnsureOnlyMatchLive();
            yield return null;
            yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[LiveAccept] MatchLive/Controller が見つからない"); yield break; }
            yield return null; // OnEnable で試合生成

            var benchName = ctrl.HomeBenchZeroName;
            Debug.Log($"[LiveAccept] 自校の代打候補: '{benchName}'");
            Directory.CreateDirectory(Dir);

            // 7回に到達するまで進める。
            var guard = 0;
            while (guard++ < 400 && ctrl.AdvanceForCapture())
            {
                var pa = ctrl.CurrentPa;
                if (pa != null && pa.Inning >= 7) break;
            }
            var atPinch = ctrl.CurrentPa;
            Debug.Log($"[LiveAccept] 7回到達: {atPinch?.Inning}回{(atPinch != null && atPinch.IsTop ? "表" : "裏")} 打者={atPinch?.BatterName}");

            // 代打を送る（自校 home）。
            var ok = ctrl.RequestPinchHitHome();
            Debug.Log($"[LiveAccept] 代打指示: {(ok ? "成功" : "失敗")}");
            yield return Capture(ctrl, "live-accept-1-pinchhit-called");

            // 以降の打席を進め、代打が打席に立つのを探す。
            var appeared = false;
            guard = 0;
            while (guard++ < 400 && ctrl.AdvanceForCapture())
            {
                var pa = ctrl.CurrentPa;
                if (pa == null) break;
                Debug.Log($"[LiveAccept] {pa.Inning}回{(pa.IsTop ? "表" : "裏")} 打者={pa.BatterName}"
                    + (pa.BatterName == benchName ? "  ← 代打！" : ""));
                if (pa.BatterName == benchName)
                {
                    appeared = true;
                    yield return Capture(ctrl, "live-accept-2-pinchhitter-bats");
                    break;
                }
            }
            Debug.Log($"[LiveAccept] 代打が打席に反映: {(appeared ? "YES" : "NO")}");

            // 最後まで進める（完走確認）。
            guard = 0;
            while (guard++ < 800 && ctrl.AdvanceForCapture()) { }
            Debug.Log($"[LiveAccept] 試合終了: gameOver={ctrl.IsGameOver}");
            yield return Capture(ctrl, "live-accept-3-final");

            Debug.Log("[LiveAccept] DONE appeared=" + appeared);
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
                Debug.Log("[LiveAccept] wrote " + file);
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

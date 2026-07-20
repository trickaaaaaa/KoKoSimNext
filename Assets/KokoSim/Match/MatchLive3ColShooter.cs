#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】MatchLive 全景3カラム＋マッチアップHUD の受け入れスクショ。
    /// 攻守交代でハイライトが左右の列を移る／代打で該当行入替／今日の成績更新／球数増加／HUD切替、を
    /// 1試合通しで撮る。使い捨て。α後に削除してよい。
    /// </summary>
    public sealed class MatchLive3ColShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/match2d-live/3col";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[3Col] MatchLive/Controller が見つからない"); yield break; }
            yield return null; // OnEnable で試合生成
            Directory.CreateDirectory(Dir);

            var bench = ctrl.HomeBenchZeroName;
            Debug.Log($"[3Col] 代打候補='{bench}'");

            yield return Capture(ctrl, "00-start");

            // 表の打席（away=相手=右列がハイライト）を探して撮る。
            yield return AdvanceUntil(ctrl, pa => pa.IsTop, 60);
            yield return Capture(ctrl, "01-top-right-highlight");

            // 裏の打席（home=自校=左列がハイライト）を探して撮る＝攻守交代でハイライトが移る（①）。
            yield return AdvanceUntil(ctrl, pa => !pa.IsTop, 60);
            yield return Capture(ctrl, "02-bottom-left-highlight");

            // 数打席消化して今日の成績・球数を溜める（③⑥）。
            yield return AdvanceUntil(ctrl, pa => pa.Inning >= 4, 120);
            yield return Capture(ctrl, "03-midgame-stats");

            // 7回まで進めて自校（home）へ代打（②）。
            yield return AdvanceUntil(ctrl, pa => pa.Inning >= 7, 200);
            var ok = ctrl.RequestPinchHitHome();
            Debug.Log($"[3Col] 代打指示={(ok ? "成功" : "失敗")}");
            yield return Capture(ctrl, "04-pinchhit-called");

            // 代打が打席に立つ＝HUD打者が切替（⑧）。
            var appeared = false;
            var guard = 0;
            while (guard++ < 200 && ctrl.AdvanceForCapture())
            {
                var pa = ctrl.CurrentPa;
                if (pa == null) break;
                if (pa.BatterName == bench) { appeared = true; break; }
            }
            Debug.Log($"[3Col] 代打が打席に反映={(appeared ? "YES" : "NO")}");
            yield return Capture(ctrl, "05-pinchhitter-bats");

            // 終盤（球数が閾値超で警告色 ⑥・奪三振累積 ⑤）。
            yield return AdvanceUntil(ctrl, pa => pa.Inning >= 9, 200);
            yield return Capture(ctrl, "06-late-pitchcount");

            guard = 0;
            while (guard++ < 400 && ctrl.AdvanceForCapture()) { }
            yield return Capture(ctrl, "07-final");
            Debug.Log($"[3Col] DONE gameOver={ctrl.IsGameOver} appeared={appeared}");
        }

        private static IEnumerator AdvanceUntil(MatchLiveController ctrl, System.Func<LivePlateAppearance, bool> cond, int max)
        {
            var guard = 0;
            while (guard++ < max && ctrl.AdvanceForCapture())
            {
                var pa = ctrl.CurrentPa;
                if (pa != null && cond(pa)) yield break;
            }
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
                Debug.Log("[3Col] wrote " + file);
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

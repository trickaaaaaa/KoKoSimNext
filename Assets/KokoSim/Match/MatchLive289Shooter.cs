#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #289 の受け入れスクショ。
    ///  1. 自校守備中 …… 盤面拡大＋指示パネル（配球グループだけが出る）
    ///  2. 自校攻撃中 …… 指示パネルが打撃グループへ切り替わり、選択タイルが点灯する
    ///  3. クリック中断ポップアップ …… 伝令/交代/スキップ/再開の4操作＋局面サマリ
    /// 確認後に削除してよい。
    /// </summary>
    public sealed class MatchLive289Shooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/i289";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;   // 自動連続進行を止め AdvanceForCapture で手動駆動
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[289Shot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            bool gotDef = false, gotOff = false, gotPause = false;
            var guard = 0;
            while (guard++ < 300 && ctrl.AdvanceForCapture())
            {
                var pa = ctrl.CurrentPa;
                if (pa == null) break;
                var onOffense = ctrl.ManagerOnOffenseForCapture;

                if (!gotDef && !onOffense)
                {
                    ctrl.FreezeCurrentAtResult();
                    yield return CaptureName("i289-1-defense-signpad");
                    Debug.Log($"[289Shot] defense: {pa.Inning}回{(pa.IsTop ? "表" : "裏")}");
                    gotDef = true;
                }
                else if (!gotOff && onOffense)
                {
                    // 打撃指示を1つ選んだ状態（点灯確認）。次の Advance で自動解除される（Q12-3）。
                    ctrl.SelectPitchBattingForCapture(KokoSim.Engine.Match.Tactics.PitchBattingOverride.ForceSwing);
                    ctrl.FreezeCurrentAtResult();
                    yield return CaptureName("i289-2-offense-signpad-selected");
                    Debug.Log($"[289Shot] offense: {pa.Inning}回{(pa.IsTop ? "表" : "裏")}");
                    gotOff = true;
                }

                if (gotDef && gotOff && !gotPause)
                {
                    ctrl.OpenPauseMenuForCapture();
                    yield return CaptureName("i289-3-pause-popup");
                    Debug.Log("[289Shot] pause popup");
                    gotPause = true;
                    break;
                }
            }

            Debug.Log($"[289Shot] DONE defense={gotDef} offense={gotOff} pause={gotPause}");
        }

        private IEnumerator CaptureName(string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ctrl = FindController();
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
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
                Debug.Log("[289Shot] wrote " + file);
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

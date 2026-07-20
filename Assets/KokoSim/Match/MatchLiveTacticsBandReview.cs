#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Tactics;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】設計書15 Phase C-3「1球采配ショートカット」帯の
    /// バッチスクショ7箇条自己レビュー用。既定(無点灯)・打撃選択時・配球選択時を撮る。
    /// </summary>
    public sealed class MatchLiveTacticsBandReview : MonoBehaviour
    {
        private const int W = 900;
        private const int H = 900;
        private const string Dir = "screenshots/c3-review";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            EnsureOnlyMatchLive();
            yield return null;
            yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[C3Review] MatchLive/Controller が見つからない"); yield break; }
            yield return null; // OnEnable で試合生成
            yield return null;

            Directory.CreateDirectory(Dir);

            // 状態0: 真の初期状態（何もクリックしていない・完全無点灯のはず）
            yield return Capture(ctrl, "c3-0-untouched-full", full: true);

            // 状態1: 明示的に「任せ/通常」を選んだ状態（既定=無点灯とは別物。挙動確認用）
            ctrl.SelectPitchBattingForCapture(null);
            ctrl.SelectPitchDefenseForCapture(null);
            yield return Capture(ctrl, "c3-1-idle-full", full: true);
            yield return Capture(ctrl, "c3-1-idle-crop", full: false);

            // 状態2: 打撃「強攻」選択（点灯）
            ctrl.SelectPitchBattingForCapture(PitchBattingOverride.ForceSwing);
            yield return Capture(ctrl, "c3-2-batting-on-full", full: true);
            yield return Capture(ctrl, "c3-2-batting-on-crop", full: false);

            // 状態3: 配球「変化球」選択（点灯・打撃はそのまま）
            ctrl.SelectPitchDefenseForCapture(PitchPolicy.BreakingHeavy);
            yield return Capture(ctrl, "c3-3-both-on-full", full: true);
            yield return Capture(ctrl, "c3-3-both-on-crop", full: false);

            Debug.Log("[C3Review] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file, bool full)
        {
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

                if (full)
                {
                    File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                }
                else
                {
                    // 帯はレイアウト上、盤面の上・実況の下あたりに位置する。900x900中の上寄りの横帯を切り出す。
                    var cropH = 90;
                    var cropY = 300;
                    var crop = new Texture2D(W, cropH, TextureFormat.RGBA32, false);
                    crop.SetPixels(tex.GetPixels(0, H - cropY - cropH, W, cropH));
                    crop.Apply();
                    File.WriteAllBytes(Path.Combine(Dir, file + ".png"), crop.EncodeToPNG());
                    Destroy(crop);
                }
                Debug.Log("[C3Review] wrote " + file);
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

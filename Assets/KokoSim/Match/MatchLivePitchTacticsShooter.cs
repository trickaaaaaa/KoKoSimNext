#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Tactics;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】1球采配ショートカット（設計書15 Phase C-3）のスクショ確認用。使い捨て。
    /// 常駐帯の既定(無点灯)→選択(点灯)→次の打席へ進めた後(自動解除)の3状態を撮る。
    /// </summary>
    public sealed class MatchLivePitchTacticsShooter : MonoBehaviour
    {
        private const int W = 900;
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
            if (ctrl == null) { Debug.LogError("[PitchTacticsShooter] MatchLive/Controller が見つからない"); yield break; }
            yield return null; // OnEnable で試合生成

            Directory.CreateDirectory(Dir);

            // 打席頭（既定・無点灯）。
            ctrl.AdvanceForCapture();
            yield return Capture(ctrl, "pitch-tactics-1-rest");

            // 強攻を選択（クリックしたのと同じ状態）。次の打席が確定するまで維持される。
            ctrl.SelectPitchBattingForCapture(PitchBattingOverride.ForceSwing);
            yield return Capture(ctrl, "pitch-tactics-2-selected");

            // 次の打席へ進める→1球指示は消費され、表示は既定(無点灯)へ自動で戻る。
            ctrl.AdvanceForCapture();
            yield return Capture(ctrl, "pitch-tactics-3-consumed-and-reset");

            Debug.Log("[PitchTacticsShooter] DONE");
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
                Debug.Log("[PitchTacticsShooter] wrote " + file);
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

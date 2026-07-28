#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】痩せ強度3段（Slim0=現状/Slim1=中/Slim2=強）の比較撮影。
    /// 本番と同じ対決ビュー構図・同じ打席頭のポーズで、着衣メッシュ資産だけ差し替えて3枚撮る
    /// （専用ステージを組むより「実際の絵で比べる」のが正直な比較になる）。体型選定が済んだら削除してよい。
    /// </summary>
    public sealed class DuelSlimCompareShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots";

        private static readonly string[] Variants =
        {
            "Assets/DuelSpike/Generated/Body_Uniform_Slim0.asset",
            "Assets/DuelSpike/Generated/Body_Uniform_Slim1.asset",
            "Assets/DuelSpike/Generated/Body_Uniform_Slim2.asset",
        };

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[SlimCompare] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            for (int i = 0; i < 3; i++) ctrl.AdvanceForCapture();
            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();

            try
            {
                for (int v = 0; v < Variants.Length; v++)
                {
                    DuelStageRig.UniformMeshOverrideForCapture = Variants[v];
                    ctrl.RebuildDuelRigForCapture();
                    // 同じ見比べ点にする: 打席頭からやり直し、構え（初球ワインドアップ前）で止まった絵を撮る
                    ctrl.RestartCurrentPaForCapture();
                    for (int i = 0; i < 24; i++) yield return null;   // 量子化2コマぶん進めて初期ポーズを安定させる
                    yield return CaptureName("i-duel-slim" + v);
                }
            }
            finally
            {
                DuelStageRig.UniformMeshOverrideForCapture = null;
            }
            Debug.Log("[SlimCompare] DONE");
        }

        private IEnumerator CaptureName(string file)
        {
            EnsureOnlyMatchLive();
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
                Debug.Log("[SlimCompare] wrote " + file);
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

#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】対決ビュー（design-18 A案）配線の目視確認用バッチ撮影。
    /// 盤面→対決トグル→数秒後（12fps量子化の進み確認）→盤面復帰、の4枚を撮る。α後に削除してよい。
    /// </summary>
    public sealed class DuelViewShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[DuelShot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            // 数打席進めて盤面に生活感を出す
            for (int i = 0; i < 3; i++) ctrl.AdvanceForCapture();
            ctrl.FreezeCurrentAtResult();
            yield return CaptureName("i-duel-1-board");

            ctrl.ToggleDuelViewForCapture();
            for (int i = 0; i < 30; i++) yield return null;   // 量子化ステップを数コマ進める
            yield return CaptureName("i-duel-2-duelview");

            for (int i = 0; i < 60; i++) yield return null;
            yield return CaptureName("i-duel-3-duelview-later");

            ctrl.ToggleDuelViewForCapture();
            yield return null;
            yield return CaptureName("i-duel-4-board-back");

            // §7-2 アンカー同期の観測: 対決ビューONのまま新しい打席を始め、投球フェーズを生で流しながら
            // 約0.8s間隔で連写する（初球ワインドアップ3s→リリース→フォロー1.5s→次の振りかぶり、まで見える窓）
            ctrl.ToggleDuelViewForCapture();
            yield return null;
            if (ctrl.AdvanceForCapture())
            {
                for (int i = 0; i < 8; i++)
                {
                    for (int f = 0; f < 40; f++) yield return null;
                    yield return CaptureName("i-duel-sync-" + i);
                }
            }

            Debug.Log("[DuelShot] DONE");
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
                Debug.Log("[DuelShot] wrote " + file);
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

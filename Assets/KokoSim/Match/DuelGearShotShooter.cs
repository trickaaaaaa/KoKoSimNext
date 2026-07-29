#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】装備メッシュ差し替え（AIGear）のオンボディ検証撮影。
    /// 本番と同じ対決ビュー構図で1枚撮る（DuelSlimCompareShooter と同型）。装備確定後は削除してよい。
    /// </summary>
    public sealed class DuelGearShotShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots";

        /// <summary>出力ファイル名（拡張子なし）。連続検証で上書きしないよう起動側が変える。</summary>
        public static string FileName = "i-duel-gear-0";

        /// <summary>寄りカットの注視点[m]（本塁=原点・+z=マウンド）とFOV[度]。</summary>
        private struct Zoom
        {
            public string Name; public float AimY, AimZ, FovDeg;
            public Zoom(string n, float y, float z, float f) { Name = n; AimY = y; AimZ = z; FovDeg = f; }
        }

        private static readonly Zoom[] Zooms =
        {
            new Zoom("hands", 1.25f, 0.1f, 1.1f),      // 打者の手元（握り）
            new Zoom("mitt", 0.75f, -1.0f, 1.1f),      // 捕手のミット（しゃがみなので低め）
            // 投手のグラブ。狙いは目測でなく GloveDiagForCapture の実測値（y1.225 / z18.325）から
            new Zoom("pitcher", 1.23f, 18.3f, 0.55f),
        };

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[GearShot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            for (int i = 0; i < 3; i++) ctrl.AdvanceForCapture();
            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            ctrl.RestartCurrentPaForCapture();
            for (int i = 0; i < 24; i++) yield return null;   // 量子化2コマぶん進めて初期ポーズを安定させる
            yield return Capture(FileName);
            if (DuelStageRig.Live != null)
                Debug.Log("[GearShot] GLOVE\n" + DuelStageRig.Live.GloveDiagForCapture());

            // 寄りの検証カット。本番RTは480×270なので、握り・装着のような手元の粗さは
            // 通常構図では潰れて見えない。較正の上書きはPlay中の静的代入＝ドメインリロードを跨がないので、
            // FileName と違って確実に効く。撮り終えたら必ず戻す（rigはoverrideを持ち続けるため）
            var prev = DuelStageRig.CalibrationOverride;
            foreach (var z in Zooms)
            {
                var cal = DuelCalibration.Default;
                cal.AimY = z.AimY; cal.AimZ = z.AimZ; cal.FovDeg = z.FovDeg;
                DuelStageRig.CalibrationOverride = cal;
                if (DuelStageRig.Live != null) DuelStageRig.Live.ApplyCalibration(cal);
                for (int i = 0; i < 24; i++) yield return null;
                yield return Capture(FileName + "-" + z.Name);
            }
            DuelStageRig.CalibrationOverride = prev;
            if (DuelStageRig.Live != null)
                DuelStageRig.Live.ApplyCalibration(prev ?? DuelCalibration.Default);
            Debug.Log("[GearShot] DONE");
        }

        private IEnumerator Capture(string file)
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
                Debug.Log("[GearShot] wrote " + file);
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

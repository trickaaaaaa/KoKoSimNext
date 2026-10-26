#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Nation;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】捕手防具の黒統一（issue #659）の検証撮影。帽子色の違う高校を守備に就け、
    /// 通常構図と捕手への寄りを撮る。あわせて防具の実色を値で記録する（絵では黒と濃色の区別がつかない）。
    /// 最後に「黒い帽子の高校 → 攻守交代 → 赤い帽子の高校」と塗り替え、防具が帽子に巻き込まれないことを見る。
    /// </summary>
    public sealed class CatcherGearColorShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots";

        public static string FileName = "i659-catcher-gear";

        /// <summary>守備側の配色ID（最も明るい帽子・黒い帽子・赤い帽子）。攻撃側は白＋紺で固定。</summary>
        private static readonly string[] DefenseIds = { "white_yamabuki", "white_black", "white_red" };

        /// <summary>捕手への寄り（本塁=原点・+z=マウンド）。既定の画角は2.9度＝それより小さくして寄る。</summary>
        private const float CatcherAimY = 0.70f, CatcherAimZ = -1.0f, CatcherFov = 1.3f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[CatcherGearShot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            for (int i = 0; i < 3; i++) ctrl.AdvanceForCapture();
            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            ctrl.RestartCurrentPaForCapture();
            for (int i = 0; i < 24; i++) yield return null;
            // 撮る間は試合を止める（進めると打球の守備ビューへ切り替わり、捕手が写らない）
            ctrl.FreezeLiveForCapture(true);

            var palette = Shell.TeamColorProvider.Palette;
            var offense = palette.FindById("white_navy");
            foreach (var id in DefenseIds)
            {
                var def = palette.FindById(id);
                if (def == null || offense == null) { Debug.LogError("[CatcherGearShot] 配色IDが表に無い: " + id); continue; }
                DuelStageRig.Live?.SetTeamColors(TeamColors.FromEntry(def), TeamColors.FromEntry(offense));
                for (int i = 0; i < 12; i++) yield return null;
                yield return Capture(FileName + "-" + id);
                yield return CaptureZoom(FileName + "-" + id + "-near");
                Debug.Log("[CatcherGearShot] " + id + "（帽子 " + def.Team + "）: "
                          + DuelStageRig.Live?.CatcherGearColorDiagForCapture());
            }

            // 黒い帽子の高校が守備 → 攻守交代で赤い帽子の高校が守備。旧実装は色の一致で防具を
            // 帽子扱いに拾っていたので、ここで防具が赤くなるのが典型的な壊れ方
            var black = palette.FindById("white_black");
            var red = palette.FindById("white_red");
            if (black != null && red != null)
            {
                DuelStageRig.Live?.SetTeamColors(TeamColors.FromEntry(black), TeamColors.FromEntry(red));
                for (int i = 0; i < 12; i++) yield return null;
                DuelStageRig.Live?.SetTeamColors(TeamColors.FromEntry(red), TeamColors.FromEntry(black));
                for (int i = 0; i < 12; i++) yield return null;
                yield return CaptureZoom(FileName + "-swap-black-to-red-near");
                Debug.Log("[CatcherGearShot] 黒→交代→赤: " + DuelStageRig.Live?.CatcherGearColorDiagForCapture());
            }
            ctrl.FreezeLiveForCapture(false);
            Debug.Log("[CatcherGearShot] DONE");
        }

        private IEnumerator CaptureZoom(string file)
        {
            var prev = DuelStageRig.CalibrationOverride;
            var cal = DuelCalibration.Default;
            cal.AimY = CatcherAimY; cal.AimZ = CatcherAimZ; cal.FovDeg = CatcherFov;
            DuelStageRig.CalibrationOverride = cal;
            DuelStageRig.Live?.ApplyCalibration(cal);
            for (int i = 0; i < 12; i++) yield return null;
            yield return Capture(file);
            DuelStageRig.CalibrationOverride = prev;
            DuelStageRig.Live?.ApplyCalibration(prev ?? DuelCalibration.Default);
            for (int i = 0; i < 6; i++) yield return null;
        }

        private IEnumerator Capture(string file)
        {
            EnsureOnlyMatchLive();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ctrl = FindController();
            // 素体が真っ黒に焼ける回がある（RT差し替えと描画の噛み合わせ）。先に1回描いておく
            ctrl.DuelRigForCapture?.RenderNow();
            yield return null;
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
                Debug.Log("[CatcherGearShot] wrote " + file);
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

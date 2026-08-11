#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.NewGame
{
    /// <summary>
    /// 【開発用・エディタ限定】新設校スタート（issue #585）の受け入れスクショ。
    /// 新規ゲーム設定画面を「地区あり県／地区なし県／未入力（エラー）」の3状態で撮り、
    /// そのまま「この高校ではじめる」を押してホーム・選手一覧まで通しで撮る。使い捨て。
    /// </summary>
    public sealed class NewGameSetupShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/new-game";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // UIDocument が組み上がる前に撮ると白紙＋起動時の内容が写る（issue #582 で踏んだ罠）。
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var doc = Open("NewGameSetup");
            if (doc == null) { Debug.LogError("[NGS] NewGameSetup が見つからない"); yield break; }
            var ctrl = doc.GetComponent<NewGameSetupController>();
            if (ctrl == null) { Debug.LogError("[NGS] Controller が付いていない"); yield break; }
            yield return null; yield return null;

            // ① 未入力＝校名エラーで CTA が押せない状態
            ctrl.SetForCapture(13, "");
            yield return null; yield return null;
            yield return Capture(doc, "00-empty-name");

            // ② 地区あり県（神奈川＝4地区）＋校名入力
            ctrl.SetForCapture(13, "湘南台");
            yield return null; yield return null;
            yield return Capture(doc, "01-kanagawa");

            // ③ 地区なし県（東京都）＝地区欄が消えること
            ctrl.SetForCapture(12, "多摩川");
            yield return null; yield return null;
            yield return Capture(doc, "02-tokyo-no-district");

            // ④ 配色を送った状態（部品共有の確認）
            ctrl.SetForCapture(13, "湘南台");
            for (var i = 0; i < 3; i++) ctrl.StepColorForCapture(0, +1);
            ctrl.StepColorForCapture(4, +5);
            ctrl.StepColorForCapture(6, +1);   // 縦縞 ON
            yield return null; yield return null;
            yield return Capture(doc, "03-colors");

            // ⑤ 確定 → ホーム
            var start = doc.rootVisualElement.Q<Button>("ngs-start");
            if (start == null) { Debug.LogError("[NGS] 開始ボタンが無い"); yield break; }
            Submit(start);
            yield return null; yield return null; yield return null;

            var roster = RosterService.Active;
            var pitchers = roster.Count(p => PitcherRoleResolver.LeansPitcher(p));
            var catchers = roster.Count(p => CatcherRoleResolver.ReadsAsCatcher(p));
            Debug.Log($"[NGS] 校名={NationService.ManagerSchoolName} 県={NationService.PrefectureId}"
                      + $" 地区={(NationService.DistrictId.HasValue ? NationService.DistrictId.Value.ToString() : "なし")}"
                      + $" 部員={roster.Count}人 1年={roster.Count(p => p.Grade == 1)}"
                      + $" 2年={roster.Count(p => p.Grade == 2)} 3年={roster.Count(p => p.Grade == 3)}"
                      + $" 投手役={pitchers} 捕手={catchers}"
                      + $" 部費={ManagerService.Manager.Funds} 基金={ManagerService.Manager.FacilityFund}"
                      + $" 名声={ManagerService.Manager.Fame} 信頼={ManagerService.Manager.Trust}");

            var home = Open("HomeDashboard");
            if (home == null) { Debug.LogError("[NGS] HomeDashboard が見つからない"); yield break; }
            yield return null; yield return null;
            yield return Capture(home, "04-home");

            var list = Open("PlayerList");
            if (list == null) { Debug.LogError("[NGS] PlayerList が見つからない"); yield break; }
            yield return null; yield return null;
            yield return Capture(list, "05-players");

            Debug.Log("[NGS] DONE all shots");
        }

        private static void Submit(VisualElement target)
        {
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        private static UIDocument Open(string screen)
        {
            UIDocument target = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) target = d;
                d.gameObject.SetActive(false);
            }
            if (target != null) target.gameObject.SetActive(true);
            return target;
        }

        private IEnumerator Capture(UIDocument doc, string file)
        {
            var ps = doc.panelSettings;
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
                Debug.Log("[NGS] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }
    }
}
#endif

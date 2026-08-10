#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Management
{
    /// <summary>
    /// 【開発用・エディタ限定】経営画面（Issue #128 の支出UI）の受け入れスクショ。
    /// ①初期（全施設Lv0・部費120万）②1段買った直後 ③レベルがばらけ、資金不足と最大レベルが
    /// 同時に出る状態、の3枚を撮ってUI原則7箇条に照らす。使い捨て。
    /// </summary>
    public sealed class ManagementShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/management";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // ScreenRouter.Start が既定画面（タイトル）を出し切るまで待つ。ここを待たずに Open すると
            // 後から走る Show(DefaultScreen) に上書きされ、経営画面のつもりでタイトルを撮る。
            for (var i = 0; i < 8; i++) yield return null;

            // ① 初期状態（新規ゲームと同じ: 全施設Lv0・部費120万）。
            GameClock.Reset();
            ManagerService.Reset();
            var doc = Open("ManagementScreen");
            if (doc == null) { Debug.LogError("[MN] ManagementScreen が見つからない"); yield break; }
            yield return null; yield return null;
            Dump("initial");
            yield return Capture(doc, "00-initial");

            // ② 1段買った直後（打撃の道具 Lv1＝30万 → 部費 90万）。本番と同じ経路（コントローラの購入）を通す。
            var ctrl = doc.GetComponent<ManagementScreenController>();
            if (ctrl == null) Debug.LogError("[MN] コントローラが付いていない");
            else ctrl.Buy("gear_batting");
            yield return null; yield return null;
            Dump("after-buy");
            yield return Capture(doc, "01-after-buy");

            // ③ 資金不足と最大レベルが同時に出る状態。
            // 施設基金が育った強豪期（照明Lv2 / 室内Lv1 / トレLv3＝最大 / 基金2500万）と、
            // 部費が乏しく道具が買えない状態（部費20万）を同時に見る。
            ManagerService.Facilities.SetLevel("lighting", 2);
            ManagerService.Facilities.SetLevel("indoor", 1);
            ManagerService.Facilities.SetLevel("weight", 3);
            ManagerService.Facilities.SetLevel("gear_batting", 2);
            ManagerService.Manager.Funds = 20;
            ManagerService.Manager.FacilityFund = 2500;
            ManagerService.Manager.Fame = 60;
            doc = Open("ManagementScreen");   // OnEnable から組み直す
            yield return null; yield return null;
            Dump("mixed");
            yield return Capture(doc, "02-mixed");

            // ④ 年契約（設計書04 §1.4a）。台帳の最下段なのでスクロールして写す。
            // 既定（スカウト網Lv1）から、上げた契約・最上位の契約・未契約が同時に見える状態にする。
            ManagerService.Contracts.SetLevel("scouting", 2);
            ManagerService.Contracts.SetLevel("expedition", 1);
            ManagerService.Contracts.SetLevel("coach_batting", 1);
            doc = Open("ManagementScreen");
            yield return null; yield return null;
            ScrollToGroup(doc, "年契約");
            yield return null; yield return null;
            Debug.Log("[MN] contracts 年額=" + ManagerService.ContractEffects.AnnualCost
                      + " scoutAdd=" + ManagerService.ContractEffects.ScoutTalentAdd
                      + " away=" + ManagerService.ContractEffects.OutOfPrefectureOpponents);
            yield return Capture(doc, "03-contracts");

            Debug.Log("[MN] DONE all shots");
        }

        private static void Dump(string tag)
        {
            var f = ManagerService.Facilities;
            Debug.Log("[MN] " + tag + " funds=" + ManagerService.Manager.Funds
                + " fund=" + ManagerService.Manager.FacilityFund
                + " lighting=" + f.LevelOf("lighting") + " indoor=" + f.LevelOf("indoor")
                + " dorm=" + f.LevelOf("dorm") + " weight=" + f.LevelOf("weight")
                + " budget=" + TrainingWeek.Budget + "分");
        }

        // 台帳のスクロールを「指定の区分見出しが視野の先頭に来る位置」へ送る。
        // ScrollView.ScrollTo は最小移動なので見出しが下端に来てしまう＝オフセットを自分で計算する。
        private static void ScrollToGroup(UIDocument doc, string groupLabel)
        {
            var sv = doc.rootVisualElement.Q<ScrollView>(className: "mn-scroll");
            if (sv == null) { Debug.LogError("[MN] mn-scroll が無い"); return; }

            VisualElement head = null;
            foreach (var h in sv.contentContainer.Query<VisualElement>(className: "plate-header").ToList())
            {
                var title = h.Q<Label>(className: "plate-header__title");
                if (title != null && title.text == groupLabel) { head = h; break; }
            }
            if (head == null) { Debug.LogError("[MN] 区分が見つからない: " + groupLabel); return; }

            var y = head.worldBound.y - sv.contentContainer.worldBound.y;
            var max = Mathf.Max(0f, sv.contentContainer.layout.height - sv.contentViewport.layout.height);
            sv.scrollOffset = new Vector2(0f, Mathf.Clamp(y, 0f, max));
        }

        // 指定画面だけをアクティブにして開き直す（OnEnable を確実に走らせる）。
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
                Debug.Log("[MN] wrote " + file);
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

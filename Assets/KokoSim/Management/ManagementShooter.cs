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

            // ④ 年契約（設計書04 §1.4a）。区分タブを切り替えて写す。
            // 既定（スカウト網Lv1）から、上げた契約・最上位の契約・未契約が同時に見える状態にする。
            ManagerService.Contracts.SetLevel("scouting", 2);
            ManagerService.Contracts.SetLevel("expedition", 1);
            ManagerService.Contracts.SetLevel("coach_batting", 1);
            doc = Open("ManagementScreen");
            yield return null; yield return null;
            var ctrl2 = doc.GetComponent<ManagementScreenController>();
            if (ctrl2 == null) Debug.LogError("[MN] コントローラが付いていない");
            else ctrl2.SelectTab(3);   // 施設 / 練習道具 / 備品 / 年契約
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

        /// <summary>
        /// 撮影解像度でのレイアウト実測。どの段が何px使っていて、台帳の見える行数が何行なのかを出す。
        /// Play 前は layout が NaN なのでここ（撮影直前）でしか正しく測れない。
        /// </summary>
        private static void Measure(UIDocument doc, string tag)
        {
            var root = doc.rootVisualElement;
            var sb = new System.Text.StringBuilder();
            sb.Append("[MN-measure ").Append(tag).Append("] ");
            Section(root, "topbar", sb, "トップバー");
            Section(root, "mn-masthead", sb, "見出し");
            Section(root, "mn-ledger", sb, "資金の中身");
            Section(root, "mn-tabs", sb, "区分タブ帯");
            Section(root, "mn-thead", sb, "列見出し");
            Section(root, "mn-scroll", sb, "台帳の窓");
            Section(root, "mn-foot", sb, "合計");

            var rows = root.Query<VisualElement>(className: "mn-row").ToList();
            var tabs = root.Query<VisualElement>(className: "seg-tab").ToList();
            var rowH = rows.Count > 0 ? rows[0].layout.height : 0f;
            var tabH = tabs.Count > 0 ? tabs[0].layout.height : 0f;
            var sv = root.Q<ScrollView>(className: "mn-scroll");
            var viewport = sv == null ? 0f : sv.contentViewport.layout.height;
            var content = sv == null ? 0f : sv.contentContainer.layout.height;

            sb.Append(" | 行=").Append(rows.Count).Append("本(").Append(rowH.ToString("0")).Append("px)")
              .Append(" タブ=").Append(tabs.Count).Append("本(").Append(tabH.ToString("0")).Append("px)")
              .Append(" 窓=").Append(viewport.ToString("0")).Append("px")
              .Append(" 中身=").Append(content.ToString("0")).Append("px")
              .Append(" 見える行≒").Append(rowH > 0 ? (viewport / rowH).ToString("0.0") : "?");

            // 行が何で背が高くなっているのかを子ごとに出す（詰める先を推測で決めないため）。
            if (rows.Count > 0)
            {
                var r = rows[0];
                sb.Append("\n  行の内訳: 枠+余白=")
                  .Append((r.layout.height - r.contentRect.height).ToString("0")).Append("px");
                for (var i = 0; i < r.childCount; i++)
                {
                    var ch = r[i];
                    var cls = "";
                    foreach (var c in ch.GetClasses()) cls = cls + c + " ";
                    sb.Append(" | ").Append(cls.Trim()).Append("=").Append(ch.layout.height.ToString("0"));
                    for (var j = 0; j < ch.childCount; j++)
                        sb.Append(" (子").Append(j).Append("=").Append(ch[j].layout.height.ToString("0")).Append(")");
                }
            }
            Debug.Log(sb.ToString());
        }

        private static void Section(VisualElement root, string cls, System.Text.StringBuilder sb, string label)
        {
            var e = root.Q<VisualElement>(className: cls);
            sb.Append(label).Append("=").Append(e == null ? "?" : e.layout.height.ToString("0")).Append("px ");
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

            // 撮影解像度（1600x900）でのレイアウト実測。案を推測でなく数字で作るために出す。
            Measure(doc, file);

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

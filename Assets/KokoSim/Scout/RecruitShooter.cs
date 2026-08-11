#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Scout
{
    /// <summary>
    /// 【開発用・エディタ限定】能動勧誘（issue #588）の受け入れスクショ。
    /// 「寮なし＝タブが出ない → 寮を建てて秋にリストが届く → 対象を選んで通う → 3月の決着」を
    /// 実際の週送りで通して撮る。使い捨て。
    /// </summary>
    public sealed class RecruitShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/recruit";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var doc = Open("ScoutScreen");
            if (doc == null) { Debug.LogError("[RC] ScoutScreen が見つからない"); yield break; }
            var ctrl = doc.GetComponent<ScoutScreenController>();
            if (ctrl == null) { Debug.LogError("[RC] Controller が付いていない"); yield break; }

            // 見立てが出ないと絵が「？」だらけになるのでスカウト網は契約しておく。
            ManagerService.Contracts.SetLevel("scouting", 2);

            // ① 寮なし＝勧誘タブが出ない（枠の出所が寮だけであることの確認）
            ctrl.Select(0);
            yield return null; yield return null;
            Debug.Log("[RC] slots(no dorm)=" + RecruitService.Slots);
            yield return Capture(doc, "00-no-dorm");

            // ② 寮を建てて秋（第24週）まで送る＝中学生リストが届く
            ManagerService.Facilities.SetLevel("dorm", 1);
            ManagerService.Manager.Funds = 400;   // 訪問費を賄える額にしておく
            ManagerService.Manager.Fame = 100;    // 合流の経路を絵で確かめたいので成功しやすい条件にする
            yield return AdvanceTo(24);
            Debug.Log("[RC] week=" + GameClock.Week + " slots=" + RecruitService.Slots
                      + " list=" + (RecruitService.Current != null ? RecruitService.Current.Prospects.Count : 0)
                      + " inSeason=" + RecruitService.InSeason);
            ctrl.SelectTab(ScoutTab.Recruits);
            yield return null; yield return null;
            yield return Capture(doc, "01-list-arrived");

            // ③ 枠2つを使って対象を指定し、片方に通う（成功率が上がる／争奪戦が起きる）
            ctrl.Select(0); ctrl.ToggleTarget();
            ctrl.Select(1); ctrl.ToggleTarget();
            ctrl.Select(2); ctrl.ToggleTarget();          // 3人目は枠が無いので断られる
            Debug.Log("[RC] targeted=" + RecruitService.Current.TargetedCount
                      + " (枠 " + RecruitService.Slots + ")");
            ctrl.Select(0);
            var before = RecruitService.SuccessProbability(0);
            for (var i = 0; i < 4; i++) { ctrl.Visit(); yield return null; }
            Debug.Log("[RC] prob " + before.ToString("0.000") + " -> "
                      + RecruitService.SuccessProbability(0).ToString("0.000")
                      + " visits=" + RecruitService.Current.Prospects[0].Visits
                      + " funds=" + ManagerService.Manager.Funds);
            yield return null; yield return null;
            yield return Capture(doc, "02-visited");

            // ④ 3月まで送って決着（成功者は入部予定者へ合流する）
            var incomingBefore = ProspectService.Current?.Count ?? 0;
            yield return AdvanceTo(46);
            Debug.Log("[RC] week=" + GameClock.Week
                      + " resolved=" + RecruitService.Current.Resolved
                      + " incoming " + incomingBefore + " -> " + (ProspectService.Current?.Count ?? 0));
            for (var i = 0; i < RecruitService.Current.Prospects.Count; i++)
            {
                var p = RecruitService.Current.Prospects[i];
                if (p.Targeted) Debug.Log("[RC]  対象 " + p.Player.Name + " → " + (p.Succeeded ? "入部" : "他校"));
            }
            ctrl.SelectTab(ScoutTab.Recruits);
            ctrl.Select(0);
            yield return null; yield return null;
            yield return Capture(doc, "03-resolved");

            // ⑤ 受動枠の側（勧誘組が混ざった入部予定者）
            ctrl.SelectTab(ScoutTab.Incoming);
            yield return null; yield return null;
            yield return Capture(doc, "04-incoming-with-recruits");

            Debug.Log("[RC] DONE all shots");
        }

        private static IEnumerator AdvanceTo(int targetWeek)
        {
            var guard = 0;
            while (GameClock.Week != targetWeek && guard++ < 60)
            {
                GameClock.Advance(1);
                if (GameSession.Current.Mode != GameMode.Normal) GameSession.Current.ResetForNewGame();
                NewTeamService.Clear();
                yield return null;
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
                Debug.Log("[RC] wrote " + file);
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

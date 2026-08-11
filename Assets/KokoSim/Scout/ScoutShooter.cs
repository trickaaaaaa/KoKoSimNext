#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Scout
{
    /// <summary>
    /// 【開発用・エディタ限定】入部予定者（issue #587）の受け入れスクショ。
    /// 週送りを実際に回して「確定前 → 3月確定（スカウト網 Lv1）→ Lv3 で見え方が変わる → 4月入部で答え合わせ」
    /// の4状態を撮る。使い捨て。
    /// </summary>
    public sealed class ScoutShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/scout";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // UIDocument が組み上がる前に撮ると白紙が写る（issue #582 で踏んだ罠）。
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var doc = Open("ScoutScreen");
            if (doc == null) { Debug.LogError("[SC] ScoutScreen が見つからない"); yield break; }
            var ctrl = doc.GetComponent<ScoutScreenController>();
            if (ctrl == null) { Debug.LogError("[SC] Controller が付いていない"); yield break; }
            yield return null; yield return null;

            // ① 確定前（4月1週目）＝空状態＋確定までの残り週
            ctrl.Select(0);
            yield return null; yield return null;
            yield return Capture(doc, "00-before-confirm");

            // ② 3月1週目まで実際に週を送る（確定は GameClock.EnterWeek のフックが行う）
            yield return AdvanceTo(46);
            Debug.Log("[SC] week=" + GameClock.Week + " year=" + GameClock.YearIndex
                      + " pending=" + ProspectService.HasPending
                      + " n=" + (ProspectService.Current != null ? ProspectService.Current.Count : 0)
                      + " disclosure=" + (ProspectService.Current != null ? ProspectService.Current.Disclosure : -1)
                      + " unknownAvg=" + UnknownAverage());
            ctrl.Select(0);
            yield return null; yield return null;
            yield return Capture(doc, "01-confirmed-nocontract");

            // ③ 段を上げて引き直す（見え方だけが変わり、来る選手は変わらないことの確認）
            var namesBefore = Names();
            yield return Reconfirm(ctrl, 1);
            Debug.Log("[SC] lv1 disclosure=" + ProspectService.Current.Disclosure
                      + " unknownAvg=" + UnknownAverage()
                      + " sameProspects=" + (namesBefore == Names()));
            yield return Capture(doc, "02-confirmed-lv1");

            yield return Reconfirm(ctrl, 3);
            Debug.Log("[SC] lv3 disclosure=" + ProspectService.Current.Disclosure
                      + " unknownAvg=" + UnknownAverage()
                      + " sameProspects=" + (namesBefore == Names()));
            yield return Capture(doc, "03-confirmed-lv3");

            // ④ 4月へ跨いで入部させる（推定と実際が並ぶ＝「？が埋まった」状態）
            var rosterBefore = RosterService.Active.Count;
            yield return AdvanceTo(0);
            Debug.Log("[SC] week=" + GameClock.Week + " year=" + GameClock.YearIndex
                      + " joined=" + (ProspectService.Current != null && ProspectService.Current.Joined)
                      + " roster " + rosterBefore + " -> " + RosterService.Active.Count);
            ctrl.Select(0);
            yield return null; yield return null;
            yield return Capture(doc, "04-joined");

            Debug.Log("[SC] DONE all shots");
        }

        /// <summary>スカウト網の段を変えて予定者を引き直す（見立てだけが変わる経路の確認用）。</summary>
        private static IEnumerator Reconfirm(ScoutScreenController ctrl, int scoutingLevel)
        {
            ManagerService.Contracts.SetLevel("scouting", scoutingLevel);
            ProspectService.Reset();
            ProspectService.Confirm(GameClock.YearIndex + 1);
            ctrl.Select(0);
            yield return null; yield return null;
        }

        /// <summary>目的の週まで1週ずつ送る。途中で大会モードへ入ったら通常モードへ戻す（撮影用の素通し）。</summary>
        private static IEnumerator AdvanceTo(int targetWeek)
        {
            var guard = 0;
            while (GameClock.Week != targetWeek && guard++ < 60)
            {
                GameClock.Advance(1);
                if (GameSession.Current.Mode != GameMode.Normal) GameSession.Current.ResetForNewGame();
                NewTeamService.Clear();   // 主将指名モーダルを開いたままにしない
                yield return null;
            }
        }

        private static string Names()
        {
            var c = ProspectService.Current;
            if (c == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var r in c.Reports) sb.Append(r.Name).Append('/');
            return sb.ToString();
        }

        private static string UnknownAverage()
        {
            var c = ProspectService.Current;
            if (c == null || c.Reports.Count == 0) return "-";
            var sum = 0;
            foreach (var r in c.Reports) sum += r.UnknownCount;
            return ((double)sum / c.Reports.Count).ToString("0.00");
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
                Debug.Log("[SC] wrote " + file);
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

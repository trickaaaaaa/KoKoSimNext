#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】新チーム発足＝次期主将の指名モーダルの受け入れスクショ（issue #21）。
    /// 引退週（夏の第17週の翌週）まで週を進め、ホームに出る指名モーダルを撮る。
    /// 候補を選び替えた状態（右ペインが追従すること）と、指名確定後のホームも撮る。使い捨て。
    /// </summary>
    public sealed class NewTeamShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/new-team";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // 引退週の翌週へ。GameClock.Advance が引退＋新チーム発足フックを回し、指名待ちになる。
            GameClock.Reset();
            NewTeamService.Clear();
            GameClock.Advance(18);
            Debug.Log("[NT] week=" + GameClock.Week + " pending=" + NewTeamService.Pending
                + " candidates=" + NewTeamService.BuildView().Candidates.Count);

            var doc = Reopen();
            if (doc == null) { Debug.LogError("[NT] HomeDashboard が見つからない"); yield break; }
            yield return null; yield return null;

            var modal = doc.rootVisualElement.Q<VisualElement>("nt-modal");
            if (modal == null || modal.style.display == DisplayStyle.None)
                Debug.LogError("[NT] 指名モーダルが開いていない");
            yield return Capture(doc, "00-designation");

            // 候補を選び替える（右ペインが追従することの確認）。
            var rows = doc.rootVisualElement.Q<ScrollView>("nt-rows");
            var picked = PickRow(rows, 3);
            if (!picked) Debug.LogWarning("[NT] 候補行が3行未満（選び替えショットは同じ絵になる）");
            yield return null; yield return null;
            yield return Capture(doc, "01-designation-picked");

            // 指名を確定 → モーダルが閉じてホームへ戻る。
            var ok = doc.rootVisualElement.Q<Button>("nt-ok");
            if (ok == null) Debug.LogError("[NT] 確定ボタンが無い");
            else Submit(ok);
            yield return null; yield return null;
            Debug.Log("[NT] after confirm pending=" + NewTeamService.Pending
                + " captain=" + CaptainName());
            yield return Capture(doc, "02-home-after");

            Debug.Log("[NT] DONE all shots");
        }

        private static string CaptainName()
        {
            var c = KokoSim.Engine.Season.CaptainSelector.Current(RosterService.Roster);
            return c == null ? "(なし)" : c.Name + " " + c.Grade + "年";
        }

        // 指定 index の候補行をクリックする（UITK のイベント送出で本番と同じ経路を通す）。
        private static bool PickRow(ScrollView rows, int index)
        {
            if (rows == null) return false;
            var content = rows.contentContainer;
            if (content.childCount <= index) return false;
            Send(content[index]);
            return true;
        }

        private static void Send(VisualElement target)
        {
            using (var e = ClickEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        // Button は Clickable マニピュレータ経由なので合成 ClickEvent では発火しない。
        // キーボード/ゲームパッドの決定と同じ NavigationSubmitEvent を送って clicked を回す。
        private static void Submit(VisualElement target)
        {
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        // HomeDashboard を一度落として付け直す（OnEnable を走らせてモーダルを開かせる）。
        private static UIDocument Reopen()
        {
            UIDocument home = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                var isHome = d.gameObject.name == "HomeDashboard";
                d.gameObject.SetActive(false);
                if (isHome) home = d;
            }
            if (home != null) home.gameObject.SetActive(true);
            return home;
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
                Debug.Log("[NT] wrote " + file);
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

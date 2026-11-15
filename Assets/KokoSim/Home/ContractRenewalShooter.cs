#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Career;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】年契約の更新（issue #1034）の受け入れスクショ。
    /// 第44週から1週進めて第45週に入り、会話→カード→確認ページ→確定、次の週（総括）で年額が引かれるまでを通す。
    /// 全部未契約の年は何も出ないことも確かめる。回し方: Play 前に <c>ContractRenewalShooter.Arm()</c>。
    /// 出力は <c>screenshots/contract-renewal/</c>（Git 管理外）。ログは <c>[CR]</c>。
    /// </summary>
    public sealed class ContractRenewalShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/contract-renewal";
        private const string SpawnFlag = "KokoSim.ContractRenewalShooter.Spawn";
        private const int W = 1600;
        private const int H = 900;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~ContractRenewalAuto").AddComponent<ContractRenewalShooter>();
        }

        private static void GoToWeek(int week, int year)
        {
            GameClock.Restore(new KokoSim.Engine.Saves.SaveState { Week = week, YearIndex = year });
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            NewTeamService.Clear();
            UniformNumberService.Clear();
            EventService.Clear();

            // ── 全部未契約の年: 出ない ──
            var set = ManagerService.Contracts;
            foreach (var d in ManagerService.ContractList.Contracts) set.SetLevel(d.Id, 0);
            GoToWeek(44, 2);
            GameClock.Advance(1);
            Debug.Log("[CR] uncontracted: week=" + GameClock.Week + " pending=" + ContractRenewalService.Pending
                + " queued=" + EventService.Pending.Count);
            if (ContractRenewalService.Pending) Debug.LogError("[CR] 未契約の年なのに更新が出た");
            EventService.Clear();

            // ── 契約中の年: 出る ──
            set.SetLevel("scouting", 3);
            set.SetLevel("expedition", 1);
            set.SetLevel("coach_batting", 1);
            GoToWeek(44, 2);
            GameClock.Advance(1);
            Debug.Log("[CR] contracted: week=" + GameClock.Week + " pending=" + ContractRenewalService.Pending
                + " queued=" + EventService.Pending.Count);
            if (!ContractRenewalService.Pending) { Debug.LogError("[CR] 契約中なのに更新が出なかった"); yield break; }

            var doc = Open("HomeDashboard");
            if (doc == null) { Debug.LogError("[CR] HomeDashboard が見つからない"); yield break; }
            yield return null; yield return null;
            var root = doc.rootVisualElement;
            var novel = root.Q<VisualElement>("ev-novel");
            var modal = root.Q<VisualElement>("cr-modal");

            // 会話を読み進める（読み終えたら更新の画面が開く）。
            var shot = 0;
            yield return Capture(doc, "00-talk");
            for (var i = 0; i < 12 && novel.resolvedStyle.display != DisplayStyle.None; i++)
            {
                Click(novel);
                yield return null; yield return null;
            }
            Debug.Log("[CR] novel closed, modal=" + modal.resolvedStyle.display);
            if (modal.resolvedStyle.display == DisplayStyle.None) { Debug.LogError("[CR] 更新の画面が開かなかった"); yield break; }
            yield return Capture(doc, "01-card1");

            // スカウト網を Lv1 へ下げる。
            var body = root.Q<VisualElement>("cr-body");
            var options = Options(body);
            Debug.Log("[CR] card1 options=" + options.Count);
            Submit(options[1]);
            yield return null; yield return null;
            yield return Capture(doc, "02-card1-lv1");

            Submit(root.Q<Button>("cr-next"));
            yield return null; yield return null;
            yield return Capture(doc, "03-card2");
            Submit(root.Q<Button>("cr-next"));
            yield return null; yield return null;
            // 打撃コーチをやめる。
            Submit(Options(body)[0]);
            yield return null; yield return null;
            yield return Capture(doc, "04-card3-cancel");
            Submit(root.Q<Button>("cr-next"));
            yield return null; yield return null;
            yield return Capture(doc, "05-summary");

            // 戻るで1枚前へ→また進む（選択が残っていること）。
            Submit(root.Q<Button>("cr-back"));
            yield return null; yield return null;
            Debug.Log("[CR] back: title=" + root.Q<Label>("cr-title").text);
            Submit(root.Q<Button>("cr-next"));
            yield return null; yield return null;

            var before = ManagerService.Manager.Funds;
            var trustBefore = ManagerService.Manager.Trust;
            Submit(root.Q<Button>("cr-next"));
            yield return null; yield return null;
            Debug.Log("[CR] confirmed: pending=" + ContractRenewalService.Pending
                + " modal=" + modal.resolvedStyle.display
                + " scouting=" + set.LevelOf("scouting") + " expedition=" + set.LevelOf("expedition")
                + " coach_batting=" + set.LevelOf("coach_batting"));
            yield return Capture(doc, "06-after");

            // 総括（第46週）で選んだ段の年額が引かれる。
            var cost = ManagerService.ContractEffects.AnnualCost;
            GameClock.Advance(1);
            var c = new CareerCoefficients();
            Debug.Log("[CR] summary: week=" + GameClock.Week + " funds " + before.ToString("0.0") + " -> "
                + ManagerService.Manager.Funds.ToString("0.0") + " (contractCost=" + cost.ToString("0")
                + " trust " + trustBefore.ToString("0.0") + " -> " + ManagerService.Manager.Trust.ToString("0.0") + ")");
            Debug.Log("[CR] DONE");
        }

        private static System.Collections.Generic.List<Button> Options(VisualElement body)
        {
            var list = new System.Collections.Generic.List<Button>();
            foreach (var ch in body.Children()) if (ch is Button b) list.Add(b);
            return list;
        }

        private static void Submit(VisualElement target)
        {
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        private static void Click(VisualElement target)
        {
            using (var e = PointerDownEvent.GetPooled())
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
                Debug.Log("[CR] wrote " + file);
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

#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using KokoSim.Unity.Tournament;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #946 校名の編集の受け入れスクショ。
    ///
    /// <para>撮るもの: ①編集画面で自校を選んで改名した姿 ②他校を選んで改名した姿（8字＝上限いっぱい）
    /// ③記録室（歴代大会の優勝校が今の名前で出るか）④夏の県大会に入って2試合消化した後に、
    /// 対戦済みの相手と自校を改名したトーナメント表（記録した試合行・枠が今の名前に引き直されるか）
    /// ⑤大会展望 ⑥ホーム。パンフレット（#645）は HTML を同じフォルダへ書き出す。</para>
    ///
    /// <para>回し方: Play 中に <c>new GameObject("Issue946Shooter").AddComponent&lt;Issue946Shooter&gt;()</c>。
    /// 出力は <c>screenshots/issue-946/</c>（PNG＋書体の監査 <c>fonts.txt</c>＋<c>pamphlet.html</c>）。</para>
    /// </summary>
    public sealed class Issue946Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/issue-946";
        private const int W = 1600;
        private const int H = 900;
        private readonly System.Text.StringBuilder _fonts = new System.Text.StringBuilder();

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 4; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            SaveGameIO.ResetForNewGame();
            GameSeed.Reset(20261114UL);
            // 自校を強くして県大会を勝ち進ませる（負けると大会表示ごと閉じ、トーナメント表が撮れない）。
            foreach (var p in RosterService.Roster)
            {
                foreach (var k in AbilityKinds.All) { p.SetCap(k, 99); p.SetLevel(k, 92); }
                p.Mental = 95;
            }
            GameClock.Advance(8);

            // ── ①② 編集画面（システムタブ→「校名の編集」） ──
            SaveSlots.Entry = SaveSlotsEntry.Tab;
            var doc = Open("SaveSlots");
            if (doc == null) { Debug.LogError("[946] SaveSlots 画面が無い"); yield break; }
            yield return null;
            var ctrl = doc.GetComponent<SaveSlotsController>();
            ctrl.ShowPage(1);
            var page = ctrl.RenamePage;
            yield return null;
            Debug.Log("[946] 自校の改名 ok=" + page.ConfirmInput("港北学院") + " 自校=" + NationService.ManagerSchoolName
                      + " 元=" + NationService.ManagerSchoolGeneratedName);
            yield return Capture(doc, "01-rename-self");

            var other = NationService.Nation.InPrefecture(NationService.PrefectureId).Skip(2).First();
            page.SelectSchool(other.Id);
            Debug.Log("[946] 他校の改名 " + other.GeneratedName + " → ok=" + page.ConfirmInput("横浜みなとみらい")
                      + " 今=" + other.Name);
            yield return Capture(doc, "02-rename-other");

            // 上限超え（9字）は確定されない（入力欄は MaxStemLength で止まる）。
            Debug.Log("[946] 9字の確定 ok=" + page.ConfirmInput("一二三四五六七八九") + "（false が正）");

            // ── ③ 記録室: 記録は改名前の名前の文字列を持っているが、表示は今の名前で出る ──
            var history = GameSession.Current.History;
            history.Add(new TournamentHistoryEntry(1, TournamentKind.Summer, "決勝", false, other.Id, other.GeneratedName));
            history.Add(new TournamentHistoryEntry(1, TournamentKind.Autumn, "決勝", true,
                NationService.ManagerSchoolId, NationService.ManagerSchoolGeneratedName + "高校"));
            doc = Open("RecordsScreen");
            yield return Capture(doc, "03-records");

            // ── ④⑤ 夏の県大会: 2試合消化してから、対戦済みの相手を改名する ──
            Open("HomeDashboard");
            yield return null;
            for (var i = 0; i < 40 && GameSession.Current.Mode != GameMode.Tournament; i++)
            {
                GameClock.Advance(1);
                yield return null;
            }
            GameSession.Current.ConsumeBanner();
            int? playedOpponent = null;
            for (var m = 0; m < 2 && GameSession.Current.InTournament && !GameSession.Current.Runner.Finished; m++)
            {
                var o = GameSession.Current.PlayMatch();
                GameSession.Current.ConsumeResult();
                playedOpponent ??= o.OpponentId;
                Debug.Log("[946] 試合 " + o.RoundName + " vs " + o.OpponentName + " " + o.ManagerScore + "-" + o.OpponentScore);
            }
            if (GameSession.Current.Display == null)
            {
                Debug.LogWarning("[946] 大会を抜けた（敗退）＝トーナメント表・展望は撮れない");
            }
            else
            {
                if (playedOpponent is int oppId && NationService.SchoolById(oppId) is { } opp)
                    Debug.Log("[946] 対戦済みの相手 " + opp.Name + " → ok=" + NationService.RenameSchool(oppId, "鶴見臨港"));
                Debug.Log("[946] 自校をもう一度改名 ok=" + NationService.RenameSchool(NationService.ManagerSchoolId, "港北"));
                var view = NationService.DisplayBracket(GameSession.Current.Display.Runner.BuildBracketView());
                foreach (var row in view.Matches.Where(x => x.ManagerInvolved))
                    Debug.Log("[946] 試合行 " + row.RoundName + " " + row.WinnerName + " " + row.WinnerScore + "-" + row.LoserScore + " " + row.LoserName);

                doc = Open("TournamentPreview");
                yield return Capture(doc, "04-bracket");
                var tp = doc.GetComponent<TournamentPreviewController>();
                tp.ShowPreviewView(true);
                yield return Capture(doc, "05-preview");

                var v = new TournamentPreviewState().Build();
                var bracket = NewspaperBracketLayout.Build(view, TournamentPamphletHtml.Metrics);
                var html = TournamentPamphletHtml.Render(v, bracket, TournamentPreviewState.BuildPamphletSchools(v, bracket));
                File.WriteAllText(Path.Combine(Dir, "pamphlet.html"), html, new System.Text.UTF8Encoding(false));
            }

            // ── ⑥ ホーム ──
            doc = Open("HomeDashboard");
            yield return Capture(doc, "06-home");

            File.WriteAllText(Path.Combine(Dir, "fonts.txt"), _fonts.ToString());
            Debug.Log("[946] DONE");
        }

        /// <summary>実機と同じ経路（ScreenRouter.Show）で画面を出す。同じ画面の開き直しは別画面を挟む（OnEnable を再発火させる）。</summary>
        private static UIDocument Open(string screen)
        {
            var router = ScreenRouter.Instance;
            if (router != null)
            {
                if (screen != "HomeDashboard") router.Show("HomeDashboard");
                router.Show(screen);
            }
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) return d;
            }
            return null;
        }

        private IEnumerator Capture(UIDocument doc, string file)
        {
            if (doc == null) { Debug.LogError("[946] 画面が無い: " + file); yield break; }
            yield return null;
            yield return new WaitForEndOfFrame();
            var ps = doc.panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;
            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            try
            {
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                _fonts.Append(file).Append(": ").Append(FontAudit.Report(doc.rootVisualElement)).Append('\n');
                Debug.Log("[946] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt); Destroy(tex);
            }
        }
    }
}
#endif

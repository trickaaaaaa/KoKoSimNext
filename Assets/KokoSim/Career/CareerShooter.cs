#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.CareerMeta
{
    /// <summary>
    /// 【開発用・エディタ限定】キャリア画面（issue #586）の受け入れスクショ。
    /// 「新任＝まだ何も無い → 週を送ってドラフト指名まで到達 → 勲章が載る」を実際の週送りで通して撮る。
    /// 使い捨て。
    /// </summary>
    public sealed class CareerShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/career";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var doc = Open("CareerScreen");
            if (doc == null) { Debug.LogError("[CR] CareerScreen が見つからない"); yield break; }

            // ① 新任（実績ゼロ）＝空状態の見え方
            Debug.Log("[CR] year=" + GameClock.YearIndex + " week=" + GameClock.Week
                      + " draft=" + DraftService.History.Count);
            yield return Capture(doc, "00-rookie");

            // ② 実績を積んだ状態（通算戦績・甲子園出場は engine の記録へ直接入れる＝
            //    大会を実プレイせずに「記録が有る画面」を撮るため）
            SeedRecords();
            doc.gameObject.SetActive(false); doc.gameObject.SetActive(true);
            yield return null; yield return null;
            yield return Capture(doc, "01-records");

            // ③ ドラフト指名週まで送って勲章を作る（能力を振り切った3年を1人仕込む）
            var star = RosterService.Roster[0];
            foreach (KokoSim.Engine.Season.AbilityKind k in System.Enum.GetValues(typeof(KokoSim.Engine.Season.AbilityKind)))
            {
                star.SetLevel(k, 95);
                star.SetCap(k, 99);
            }
            star.Grade = 3;
            var starNumber = star.UniformNumber;
            Debug.Log("[CR] star=" + star.Name + " id=" + star.Id + " no=" + starNumber);

            // 大会入り（夏13週・秋23週）を跨ぐと、このハーネスの AdvanceTo が
            // GameSession.ResetForNewGame() を呼んで成績を捨てる。アーカイブを焼くのはその後にする。
            yield return AdvanceTo(24);

            // 「当時の背番号」は大会別アーカイブからしか引けない（夏の引退で返上される・issue #531）。
            // 大会を実プレイせずに絵を撮るため、3年夏の枠へ1試合ぶんだけ焼いておく。
            var stats = GameSession.Current.Stats;
            stats.StartTournament();
            stats.FoldGame(new KokoSim.Engine.Match.Game.GameResult
            {
                AwayName = "相手", HomeName = "自校", AwayRuns = 1, HomeRuns = 5,
                InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
                HomeBatting = new[]
                {
                    new KokoSim.Engine.Match.Game.BattingLine(
                        1, KokoSim.Engine.Match.Field.FieldPosition.CenterField, star.Name,
                        PlateAppearances: 24, AtBats: 20, Hits: 9, Doubles: 2, Triples: 0, HomeRuns: 3,
                        Rbi: 8, Walks: 4, StrikeOuts: 2, SourceId: star.Id, Runs: 6),
                },
            }, managerIsAway: false);
            stats.ArchiveCurrentTournament(
                KokoSim.Engine.Stats.TournamentSlot.SummerPref,
                new System.Collections.Generic.Dictionary<int, (int Grade, int UniformNumber)>
                {
                    [star.Id] = (3, starNumber),
                });

            yield return AdvanceTo(SeasonCalendarDraftWeek());
            SeedRecords();   // 大会入りのたびに ResetForNewGame が走って戦績が消えるので撮る直前に入れ直す
            Debug.Log("[CR] week=" + GameClock.Week + " draft=" + DraftService.History.Count);
            foreach (var r in DraftService.History.Records)
                Debug.Log("[CR]  " + r.Year + " #" + r.UniformNumber + " " + r.Name + " " + r.Round + "位");

            doc = Open("CareerScreen");
            yield return null; yield return null;
            yield return Capture(doc, "02-drafted");

            // ④ ホームの通知フィード。フィードはホームから週を進めたときだけ流れる（提示はホームの責務）ので、
            //    ホームを開いて1週ぶん進める＝指名週に溜まった変化がここで一気に出る。
            var home = Open("HomeDashboard");
            if (home != null)
            {
                var ctrl = home.GetComponent<KokoSim.Unity.Home.HomeDashboardController>();
                var field = ctrl != null
                    ? ctrl.GetType().GetField("_state",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    : null;
                var state = field != null ? field.GetValue(ctrl) : null;
                if (state != null)
                {
                    state.GetType().GetMethod("AdvanceWeek").Invoke(state, null);
                    home.gameObject.SetActive(false); home.gameObject.SetActive(true);
                }
                else Debug.LogWarning("[CR] HomeState を取れなかった（フィードは空のまま）");
                yield return null; yield return null;
                yield return Capture(home, "03-feed");
            }

            Debug.Log("[CR] DONE all shots");
        }

        /// <summary>通算戦績・監督メタを「積み上がった状態」にする（絵を撮るための仕込み）。</summary>
        private static void SeedRecords()
        {
            var records = GameSession.Current.Records;
            var id = NationService.ManagerSchoolId;
            var r = records.For(id);
            if (r.OfficialWins == 0)
            {
                for (var i = 0; i < 24; i++) records.RecordOfficialMatch(id, -900 - i);
                for (var i = 0; i < 9; i++) records.RecordOfficialMatch(-800 - i, id);
                records.RecordKoshienAppearance(id, KokoSim.Engine.Nation.KoshienKind.Summer, 2025);
                records.RecordKoshienAppearance(id, KokoSim.Engine.Nation.KoshienKind.Summer, 2026);
                records.RecordKoshienMatch(id, -700);
                records.RecordKoshienMatch(id, -701);
                records.RecordKoshienMatch(id, -702);
                records.RecordKoshienMatch(-703, id);
                records.RecordKoshienMatch(-704, id);
                records.UpdateBestResult(id, KokoSim.Engine.Nation.BestResult.RoundOf8);
            }

            // 監督も育っている状態にする（新任のままだと全部 G ランクで帯の見え方が確かめられない）。
            var m = ManagerService.Manager;
            m.CoachingBatting = 62; m.CoachingPitching = 41; m.CoachingDefense = 55;
            m.TacticalSense = 55; m.TalentEye = 44; m.Fame = 28; m.Trust = 55;
        }

        private static int SeasonCalendarDraftWeek() => new KokoSim.Engine.Season.SeasonCalendar().DraftWeek;

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

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Records
{
    /// <summary>
    /// 【開発用・エディタ限定】記録画面（issue #600）の受け入れスクショ。
    /// 「1年目＝まだ何も無い → 3年ぶんの大会と成績を積んだ状態」を、タブ・スコープを切り替えて撮る。
    /// 使い捨て。
    /// </summary>
    public sealed class RecordsShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/records";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var doc = Open("RecordsScreen");
            if (doc == null) { Debug.LogError("[RC] RecordsScreen が見つからない"); yield break; }

            // ① 1年目（記録ゼロ）＝空状態の見え方。
            yield return SetPage(doc, 0, 0);
            yield return Capture(doc, "00-empty-tournaments");
            yield return SetPage(doc, 1, 0);
            yield return Capture(doc, "01-empty-records");

            // ② 3年ぶんの大会・成績を積む（実プレイで3年回さずに「記録が有る画面」を撮るための仕込み）。
            Seed();
            Debug.Log("[RC] history=" + GameSession.Current.History.Count
                      + " career=" + GameSession.Current.Stats.Career.Players.Count
                      + " archiveKeys=" + Count(GameSession.Current.Stats.Archive.Keys));

            yield return SetPage(doc, 0, 0);
            yield return Capture(doc, "02-tournaments");
            yield return SetPage(doc, 1, 0);
            yield return Capture(doc, "03-records-career");
            yield return SetPage(doc, 1, 1);
            yield return Capture(doc, "04-records-season");

            Debug.Log("[RC] DONE all shots");
        }

        private static int Count(IEnumerable<TournamentArchiveKey> keys)
        {
            var n = 0;
            foreach (var _ in keys) n++;
            return n;
        }

        // ── 仕込み ──────────────────────────────────────────────

        private static void Seed()
        {
            var session = GameSession.Current;
            var id = NationService.ManagerSchoolId;

            // 通算戦績（学校の通算カード）。
            var records = session.Records;
            for (var i = 0; i < 21; i++) records.RecordOfficialMatch(id, -900 - i);
            for (var i = 0; i < 8; i++) records.RecordOfficialMatch(-800 - i, id);
            records.RecordKoshienAppearance(id, KoshienKind.Summer, 2027);
            records.RecordKoshienMatch(id, -700);
            records.RecordKoshienMatch(id, -701);
            records.RecordKoshienMatch(-702, id);
            records.UpdateBestResult(id, BestResult.RoundOf8);

            // 歴代大会結果（年度別）。優勝した年・準優勝の年・敗退の年を混ぜる。
            var history = session.History;
            history.Add(new TournamentHistoryEntry(1, TournamentKind.Summer, "3回戦", false, 220, "弘学学園"));
            history.Add(new TournamentHistoryEntry(1, TournamentKind.Autumn, "準々決勝", false, 231, "横浜聖陵"));
            history.Add(new TournamentHistoryEntry(2, TournamentKind.Summer, "決勝", false, 220, "弘学学園"));
            history.Add(new TournamentHistoryEntry(2, TournamentKind.Autumn, "準決勝", false, 245, "相模第一"));
            history.Add(new TournamentHistoryEntry(3, TournamentKind.Summer, "決勝", true, id, TopBarMeters.SchoolDisplayName()));

            // 個人成績。3年ぶんの大会を「今大会→アーカイブ」で回し、通算（練習試合込み）も積む。
            var stats = session.Stats;
            SeedSeason(stats, year: 1, games: 5, spread: 0);
            SeedSeason(stats, year: 2, games: 6, spread: 1);
            SeedSeason(stats, year: 3, games: 7, spread: 2);
        }

        /// <summary>1シーズンぶん（大会＋練習試合）を積み、大会ぶんだけアーカイブへ焼く。</summary>
        private static void SeedSeason(PlayerStatStore stats, int year, int games, int spread)
        {
            var roster = RosterService.Roster;
            if (roster.Count < 6) { Debug.LogWarning("[RC] 部員が足りない"); return; }

            stats.StartTournament();
            for (var g = 0; g < games; g++) stats.FoldGame(Game(roster, year, g, spread), managerIsAway: false);

            // 当時の学年・背番号（アーカイブのキーと表示）。年度が進むほど学年が上がる並びにする。
            var info = new Dictionary<int, (int Grade, int UniformNumber)>();
            for (var i = 0; i < 6; i++)
            {
                var grade = 1 + ((year + i) % 3);
                info[roster[i].Id] = (grade, i + 1);
            }
            stats.ArchiveCurrentTournament(TournamentSlot.SummerPref, info, year);

            // 練習試合ぶん（通算にだけ乗る＝「通算＝練習試合を含む」の見え方を確かめる）。
            for (var g = 0; g < 3; g++)
                stats.FoldGame(Game(roster, year, games + g, spread), managerIsAway: false, isOfficial: false);
        }

        /// <summary>1試合ぶんの箱スコア（決め打ちの疑似値。選手ごとに差が出るよう添字で振る）。</summary>
        private static GameResult Game(IReadOnlyList<KokoSim.Engine.Season.DevelopingPlayer> roster,
            int year, int g, int spread)
        {
            // 選手ごとに得意分野をずらす（1人が全部門を独占すると帯の見え方が確かめられない）。
            var batting = new List<BattingLine>();
            for (var i = 0; i < 6; i++)
            {
                var p = roster[i];
                var seed = g + i * 3 + spread;
                var hits = i == 1 ? (seed % 3 == 0 ? 3 : 2)          // 打率・安打型
                    : i == 2 ? (seed % 2 == 0 ? 2 : 1)               // 中距離
                    : seed % 4 == 0 ? 2 : seed % 3 == 0 ? 1 : 0;
                var hr = i == 3 && seed % 2 == 0 ? 1                 // 長距離砲
                    : i == 2 && seed % 5 == 0 ? 1 : 0;
                var sb = i == 4 ? (seed % 2 == 0 ? 2 : 1) : 0;       // 走り屋
                batting.Add(new BattingLine(
                    i + 1, FieldPosition.CenterField, p.Name,
                    PlateAppearances: 5, AtBats: 4, Hits: hits, Doubles: hr == 0 && hits > 1 ? 1 : 0,
                    Triples: 0, HomeRuns: hr, Rbi: hits + hr * 2, Walks: 1, StrikeOuts: 1,
                    SourceId: p.Id, StolenBases: sb, Runs: hits));
            }

            // 投手は3人（先発2枚＋抑え）。先発を年度でずらして防御率と投球回の1位が分かれるようにする。
            var aceIndex = (g + spread) % 2 == 0 ? 0 : 5;
            var pitching = new List<PitchingLine>
            {
                new PitchingLine("先発", Outs: 18 + spread, BattersFaced: 24, Hits: 4 + (g % 3),
                    Runs: 1 + (g % 2), StrikeOuts: 6 + (g % 4), Walks: 1, Pitches: 95,
                    SourceId: roster[aceIndex].Id, EarnedRuns: 1 + (g % 2)),
                new PitchingLine("抑え", Outs: 6, BattersFaced: 8, Hits: 1 + (g % 2),
                    Runs: g % 3 == 0 ? 1 : 0, StrikeOuts: 3, Walks: 0, Pitches: 30,
                    SourceId: roster[1].Id, EarnedRuns: g % 3 == 0 ? 1 : 0),
            };

            return new GameResult
            {
                AwayName = "相手", HomeName = "自校", AwayRuns = 2, HomeRuns = 5,
                InningsPlayed = 9, TotalPitches = 125, PitcherChanges = 1,
                HomeBatting = batting, HomePitching = pitching,
            };
        }

        // ── タブ操作（選択状態は画面が private で持つので反射で据える） ──

        private IEnumerator SetPage(UIDocument doc, int page, int scope)
        {
            var ctrl = doc.GetComponent<RecordsScreenController>();
            var t = ctrl.GetType();
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            t.GetField("_page", F).SetValue(ctrl, page);
            t.GetField("_scope", F).SetValue(ctrl, scope);
            doc.gameObject.SetActive(false);
            doc.gameObject.SetActive(true);
            yield return null;
            yield return null;
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

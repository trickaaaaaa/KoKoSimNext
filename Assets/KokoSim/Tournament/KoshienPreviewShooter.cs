#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 【開発用・エディタ限定】夏の甲子園の大会展望の受け入れスクショ（設計書05 §1.1a）。
    /// 49代表の甲子園を組み立て、代表校の県予選成績を実際に打ち直してから
    /// 「展望トップ → 出場校の名簿（N年ぶりN回目）→ 右ペインの県予選成績 → 甲子園タブ → 樹形図」を撮る。
    /// 最後に自校が出場していない年（観るだけの甲子園）も撮る。使い捨て。
    /// </summary>
    public sealed class KoshienPreviewShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/koshien-preview";
        private const int PrefectureId = 13;   // 神奈川（HomeState と同じ）
        private const int ManagerSchoolId = -1;
        private const int RepCount = 49;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            EnsureOnly("TournamentPreview");
            yield return null; yield return null;
            EnsureOnly("TournamentPreview");
            var doc = FindDoc("TournamentPreview");
            if (doc == null) { Debug.LogError("[KO] TournamentPreview が見つからない"); yield break; }

            var ctrl = doc.GetComponent<TournamentPreviewController>();
            if (ctrl == null) { Debug.LogError("[KO] Controller が見つからない"); yield break; }

            // ── 1. 自校が出場していない年（観るだけの甲子園）──
            // <b>先に撮る</b>: GameSession.Display は対話中の大会を優先するので、対話モードへ入った後に
            // 観るだけの甲子園を据えても対話中の大会に隠れる（実フローでは県大会が終わって
            // Mode=Normal になってから片方だけが始まるので排他）。
            SetUp(includeManager: false);
            yield return Reopen(doc);
            var scroll0 = doc.rootVisualElement.Q<ScrollView>();
            if (scroll0 != null) scroll0.scrollOffset = Vector2.zero;
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "05-spectated-bracket");

            ctrl.ShowPreviewView(true);
            yield return null;
            if (scroll0 != null) scroll0.scrollOffset = new Vector2(0, 1500);
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "06-spectated-entrants");

            // ── 2. 自校が出場する甲子園 ──
            SetUp(includeManager: true);
            yield return Reopen(doc);

            ctrl.ShowPreviewView(true);
            yield return null;
            var scroll = doc.rootVisualElement.Q<ScrollView>();
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "01-preview-top");

            // 出場校の名簿（左＝49代表 ／ 右＝選んだ1校の県予選成績）。
            if (scroll != null) scroll.scrollOffset = new Vector2(0, 1500);
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "02-entrants-prefqualifier");

            // 成績スコープを「甲子園」へ切り替えた状態（まだ試合が無い＝「まだ試合なし」）。
            var tabs = doc.rootVisualElement.Q<VisualElement>("tp-ed-tabs");
            var koshienTab = tabs != null && tabs.childCount > 0 ? tabs[0].childCount > 1 ? tabs[0][1] : null : null;
            if (koshienTab != null) Click(koshienTab);
            yield return null;
            if (scroll != null) scroll.scrollOffset = new Vector2(0, 1500);
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "03-entrants-koshien-tab");

            // 樹形図（49代表＝6ラウンド）。
            ctrl.ShowPreviewView(false);
            yield return null;
            if (scroll != null) scroll.scrollOffset = new Vector2(0, 240);
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "04-bracket");

            Debug.Log("[KO] DONE all shots");
        }

        /// <summary>画面を組み直す（OnEnable を再実行させる）。</summary>
        private static IEnumerator Reopen(UIDocument doc)
        {
            doc.gameObject.SetActive(false);
            yield return null;
            doc.gameObject.SetActive(true);
            yield return null; yield return null;
        }

        // ===== 大会の組み立て =====

        private static void SetUp(bool includeManager)
        {
            // 2ケースを1セッションで撮るので、毎回まっさらから組む。
            // ①出場回数が前のケースぶん積み増されない（初出場の校が「2回目」になる）
            // ②大会モードが Normal へ戻る＝観るだけの甲子園が対話中の大会に隠れない
            GameSession.Current.ResetForNewGame();

            var coeff = new NationCoefficients();
            var nation = NationGenerator.Generate(
                SchoolNameVocabProvider.Default, coeff, new Xoshiro256Random(2026));

            // 各県の強さ1位を代表に見立てる（実フローの「県大会優勝」の代わり）。
            var reps = new List<School>(RepCount);
            foreach (var pref in nation.Prefectures)
            {
                School best = null;
                foreach (var s in nation.InPrefecture(pref.Id))
                    if (best == null || s.Strength > best.Strength) best = s;
                if (best != null) reps.Add(best);
                if (reps.Count >= RepCount) break;
            }

            if (includeManager)
            {
                // 自県の代表を自校で置き換える（県大会を勝ち抜いた形）。
                var manager = new School
                {
                    Id = ManagerSchoolId, Name = "桜丘", PrefectureId = PrefectureId,
                    Strength = TeamOverall.Of(RosterService.Active),
                };
                reps.RemoveAll(s => s.PrefectureId == PrefectureId);
                reps.Add(manager);
            }

            var records = GameSession.Current.Records;
            var stats = NationService.TournamentStats;
            stats.StartTournament();
            // 今大会（空）を県予選スコープへ写す＝前回の撮影ぶんを消す（公開APIだけで初期化する）。
            stats.SnapshotPrefQualifier();

            // 「N年ぶりN回目」に幅を持たせる（初出場・2年連続・N年ぶりが名簿に混ざるように）。
            // 実フローと同じく<b>1年につき1回だけ</b>積む（同じ年に2度積むと「0年ぶり」になる）。
            for (var i = 0; i < reps.Count; i++)
            {
                var s = reps[i];
                var prior = i % 4;                      // 0=初出場 / 1〜3=過去に出ている
                var year = 2028 - (1 + i % 5);          // 直近の出場年（2023〜2027）
                for (var k = prior; k > 0; k--)
                    records.RecordKoshienAppearance(s.Id, KoshienKind.Summer, year - (k - 1) * 2);
                records.RecordKoshienAppearance(s.Id, KoshienKind.Summer, 2028);
            }

            // 代表校の県予選成績を実際に打ち直す（先頭6校ぶん＝名簿の上から見える範囲）。
            FillPrefQualifier(reps, stats);

            var title = "2028年 全国高等学校野球選手権大会";
            var schedule = new TournamentSchedule();
            GameSession.Current.Year = 1;
            if (includeManager)
            {
                var manager = reps.Find(s => s.Id == ManagerSchoolId);
                var runner = new TournamentRunner(
                    reps, manager, coeff, new Xoshiro256Random(9999), schedule, title,
                    new PlayerMatchResolver(), isNationalTournament: true);
                GameSession.Current.EnterTournament(TournamentKind.SummerKoshien, title, runner, reps);
                GameSession.Current.ConsumeBanner();
            }
            else
            {
                var runner = TournamentRunner.Spectated(
                    reps, coeff, new Xoshiro256Random(9999), schedule, title, isNationalTournament: true);
                GameSession.Current.EnterSpectatedKoshien(title, runner, reps);
            }
        }

        /// <summary>
        /// 名簿の上から見える校ぶんだけ県予選成績を作る（全49校を打つと撮影が長すぎる）。
        /// <b>名簿と同じ並び（区画Id順＝北から南へ）の先頭から</b>打つ＝既定で選択される校の表が埋まる。
        /// </summary>
        private static void FillPrefQualifier(List<School> repsInput, KokoSim.Engine.Stats.NationTournamentStats stats)
        {
            var reps = new List<School>(repsInput);
            reps.Sort((a, b) =>
            {
                var c = a.PrefectureId.CompareTo(b.PrefectureId);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });

            var resolver = new BackgroundMatchResolver(
                NationService.Rosters, new GameContext(), yearIndex: 1, stats: null);
            var rng = new Xoshiro256Random(777);
            var schedule = new TournamentSchedule();

            for (var i = 0; i < reps.Count && i < 8; i++)
            {
                var champion = reps[i];
                if (champion.Id == ManagerSchoolId) continue;   // 自校は自校ストア側の話なので触らない
                var path = new List<ChampionMatchup>();
                for (var r = 3; r >= 1; r--)
                    path.Add(new ChampionMatchup(reps[(i + r) % reps.Count].Id, r));

                var result = new PrefectureResult(
                    champion.PrefectureId, "", champion.Name, null, champion.Id, path, EntrantCount: 32);
                QualifierStatsSim.ReplayChampionPath(
                    result, id => Find(reps, id), resolver, stats, schedule,
                    QualifierStatsSim.RoundsFor(32), yearIndex: 1, rng);
            }
        }

        private static School Find(IReadOnlyList<School> list, int id)
        {
            foreach (var s in list) if (s.Id == id) return s;
            return null;
        }

        private static void Click(VisualElement e)
        {
            using (var ev = PointerDownEvent.GetPooled())
            {
                ev.target = e;
                e.SendEvent(ev);
            }
        }

        // ===== 撮影（TournamentPreviewShooter と同じ作法） =====

        private IEnumerator Capture(UIDocument doc, int w, int h, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ps = doc.panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(w, h);
            ps.targetTexture = rt;

            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[KO] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        private static void EnsureOnly(string name)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == name);
            }
        }

        private static UIDocument FindDoc(string name)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == name) return d;
            }
            return null;
        }
    }
}
#endif

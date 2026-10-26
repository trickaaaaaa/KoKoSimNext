#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Lineup
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #656（DH制時の先発投手の欄を打順行と同じ情報量・級数にする）の確認撮影。
    /// DH制OFF/ON、先発を選択中、成績あり/なしを撮る。成績は撮影の間だけ通算帳簿に投手成績を足して出す。
    /// </summary>
    public sealed class Issue656Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/i656";
        private const int PrefectureId = 13;   // 神奈川（HomeState と同じ）
        private const int W = 1600;
        private const int H = 900;

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 10; i++) yield return null;

            SetUpTournament();

            MonoBehaviour lineup = null;
            yield return Focus("LineupSetting", r => lineup = r);
            if (lineup == null) { Debug.LogError("[i656] LineupSetting が見つからない"); yield break; }

            Select(lineup, picked: -1, hovered: -1, tab: 0);
            yield return Capture(lineup, "01-dh-off");

            // DH制ON（成績なし＝「-」の並び）
            ToggleDh(lineup);
            Select(lineup, picked: -1, hovered: -1, tab: 0);
            yield return Capture(lineup, "02-dh-on-nostats");

            // 成績あり（通算 42 1/3回・自責11・奪三振45）
            AddPitchingToAll();
            Select(lineup, picked: -1, hovered: -1, tab: 0);
            yield return Capture(lineup, "03-dh-on-stats");

            // 先発を選択中（差し替え待ち）の見え方
            var state = lineup.GetType().GetField("_state", Priv)?.GetValue(lineup) as LineupSettingState;
            state?.ClickStartingPitcher();
            lineup.GetType().GetMethod("Render", Priv)?.Invoke(lineup, null);
            yield return Capture(lineup, "04-dh-on-picked");

            Debug.Log("[i656] DONE");
        }

        /// <summary>撮影用: 部員全員の通算帳簿に投手成績を1試合ぶん足す（StatBook.For は非公開＝反射で呼ぶ）。</summary>
        private static void AddPitchingToAll()
        {
            var book = GameSession.Current.Stats.Career;
            var forM = book.GetType().GetMethod("For", Priv);
            if (forM == null) { Debug.LogError("[i656] StatBook.For が見つからない"); return; }
            foreach (var p in RosterService.Active)
            {
                var ps = forM.Invoke(book, new object[] { p.Id }) as KokoSim.Engine.Stats.PlayerStats;
                ps?.Pitching.Add(new KokoSim.Engine.Match.Game.PitchingLine(
                    p.Name, Outs: 127, BattersFaced: 170, Hits: 35, Runs: 13, StrikeOuts: 45, Walks: 12, Pitches: 640,
                    SourceId: p.Id, EarnedRuns: 11), started: true);
            }
        }

        // 比較の左右（picked=左, hovered=右）とタブを差し込んで再描画する。
        private static void Select(MonoBehaviour ctrl, int picked, int hovered, int tab)
        {
            var state = ctrl.GetType().GetField("_state", Priv)?.GetValue(ctrl);
            if (state == null) return;
            state.GetType().GetField("_picked", Priv)?.SetValue(state, picked);
            state.GetType().GetField("_hovered", Priv)?.SetValue(state, hovered);
            state.GetType().GetField("_tab", Priv)?.SetValue(state, tab);
            ctrl.GetType().GetMethod("Render", Priv)?.Invoke(ctrl, null);
        }

        private static void ToggleDh(MonoBehaviour ctrl)
        {
            var state = ctrl.GetType().GetField("_state", Priv)?.GetValue(ctrl) as LineupSettingState;
            state?.ToggleDh();
        }

        private static void SetUpTournament()
        {
            var roster = RosterService.Active;
            var manager = new School
            {
                Id = -1,
                Name = NationService.ManagerSchoolName,
                PrefectureId = PrefectureId,
                Strength = TeamOverall.Of(roster),
            };

            var coeff = new NationCoefficients();
            var nation = NationGenerator.Generate(
                SchoolNameVocabProvider.Default, coeff, new Xoshiro256Random(2026));

            var field = new List<School> { manager };
            foreach (var s in nation.InPrefecture(PrefectureId))
            {
                if (s.Id == manager.Id) continue;
                field.Add(s);
            }

            var runner = new TournamentRunner(
                field, manager, coeff, new Xoshiro256Random(4242), new TournamentSchedule(),
                "2027年 夏の神奈川県大会", new PlayerMatchResolver());

            GameSession.Current.Year = 1;
            GameSession.Current.EnterTournament(
                TournamentKind.Summer, "2027年 夏の神奈川県大会", runner, field);
            GameSession.Current.ConsumeBanner();
        }

        private static void SetUpLineup(bool useDh)
        {
            var state = new LineupSettingState();
            if (useDh) state.ToggleDh();
            GameSession.Current.Lineup = state.ToLineupSpec();
        }

        private IEnumerator Capture(MonoBehaviour ctrl, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();

            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
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
                Debug.Log("[i656] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        // ScreenRouter 経由で切り替えたうえで、他画面の UIDocument が同フレームに割り込まないよう再保証する。
        private static IEnumerator Focus(string screen, System.Action<MonoBehaviour> onReady)
        {
            if (ScreenRouter.Instance != null) ScreenRouter.Instance.Show(screen);
            var ctrl = Activate(screen);
            yield return null; yield return null;
            ctrl = Activate(screen) ?? ctrl;   // ルータが同フレームで別画面を出しても取り戻す
            yield return null; yield return null;
            onReady(ctrl);
        }

        // 対象画面だけを表示する（他画面の UIDocument が重なると描画が競合する）。
        private static MonoBehaviour Activate(string screen)
        {
            MonoBehaviour found = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                var on = d.gameObject.name == screen;
                d.gameObject.SetActive(on);
                if (!on) continue;
                foreach (var mb in d.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.GetType().Name.EndsWith("Controller")) found = mb;
            }
            return found;
        }
    }
}
#endif

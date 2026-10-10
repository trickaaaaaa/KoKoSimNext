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
    /// 【開発用・エディタ限定】勝敗投手概念の撤去（2026-08-29）の受け入れスクショ。
    /// スタメン設定の比較カードで「勝敗 N勝N敗」が消え、WHIPに差し替わっていることを確認する。
    /// 使い捨て。確認後に削除してよい。
    /// </summary>
    public sealed class WinLossRemovalLineupShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/win-loss-removal";
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

            for (var i = 0; i < 40 && GameSession.Current.InTournament && !GameSession.Current.Runner.Finished; i++)
            {
                if (GameSession.Current.ReachedMatchDay) { GameSession.Current.PlayMatch(); GameSession.Current.ConsumeResult(); }
                else GameSession.Current.AdvanceDay();
                yield return null;
            }
            Debug.Log("[wlr] 成績を持つ選手数 career=" + GameSession.Current.Stats.Career.Players.Count);

            MonoBehaviour lineup = null;
            yield return Focus("LineupSetting", r => lineup = r);
            if (lineup == null) { Debug.LogError("[wlr] LineupSetting が見つからない"); yield break; }

            Select(lineup, picked: PitcherRow(lineup), hovered: 0, tab: 0);
            yield return Capture(lineup, "00-lineup-cmp");

            var root = lineup.GetComponent<UIDocument>().rootVisualElement;
            var statHost = root.Q<VisualElement>("lu-cmp-stats");
            if (statHost != null)
            {
                for (var e = statHost.parent; e != null; e = e.parent)
                    if (e is ScrollView sv) { sv.ScrollTo(statHost); break; }
                yield return null; yield return null;
                yield return Capture(lineup, "01-lineup-cmp-scrolled");
            }
            else Debug.LogError("[wlr] lu-cmp-stats が見つからない");

            Debug.Log("[wlr] DONE");
        }

        // MetricsFor の「hasPitching && !hasBatting」枝（WHIP行が出る枝）を実際に踏む選手を探す。
        // 投手成績はあるが打席に一度も立っていない選手（登板が打順を回る前に試合終了した救援投手等）。
        // 見つからなければ投球回が最大の選手にフォールバックする（枝は踏めないが落ちはしない）。
        private static int PitcherRow(MonoBehaviour ctrl)
        {
            var state = ctrl.GetType().GetField("_state", Priv)?.GetValue(ctrl);
            var roster = state?.GetType().GetField("_roster", Priv)?.GetValue(state)
                as IReadOnlyList<KokoSim.Engine.Season.DevelopingPlayer>;
            if (roster == null) return 0;

            var book = GameSession.Current.Stats.Career;
            var pureFallback = -1;
            var bestOuts = 0;
            for (var i = 0; i < roster.Count; i++)
            {
                var s = book.Get(roster[i].Id);
                if (s == null) continue;
                var hasPitching = s.Pitching.Games > 0 || s.Pitching.BattersFaced > 0;
                var hasBatting = s.Batting.Games > 0 || s.Batting.PlateAppearances > 0;
                if (hasPitching && !hasBatting)
                {
                    Debug.Log("[wlr] 投手専業（打席無し）row=" + i + " outs=" + s.Pitching.Outs);
                    return i;
                }
                if (s.Pitching.Outs > bestOuts) { bestOuts = s.Pitching.Outs; pureFallback = i; }
            }
            Debug.LogWarning("[wlr] 投手専業（打席無し）の選手が見つからず。投球回最大にフォールバック row="
                + pureFallback + " outs=" + bestOuts);
            return pureFallback >= 0 ? pureFallback : 0;
        }

        private static void Select(MonoBehaviour ctrl, int picked, int hovered, int tab)
        {
            var state = ctrl.GetType().GetField("_state", Priv)?.GetValue(ctrl);
            if (state == null) return;
            state.GetType().GetField("_picked", Priv)?.SetValue(state, picked);
            state.GetType().GetField("_hovered", Priv)?.SetValue(state, hovered);
            state.GetType().GetField("_tab", Priv)?.SetValue(state, tab);
            ctrl.GetType().GetMethod("Render", Priv)?.Invoke(ctrl, null);
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
                Debug.Log("[wlr] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        private static IEnumerator Focus(string screen, System.Action<MonoBehaviour> onReady)
        {
            if (ScreenRouter.Instance != null) ScreenRouter.Instance.Show(screen);
            var ctrl = Activate(screen);
            yield return null; yield return null;
            ctrl = Activate(screen) ?? ctrl;
            yield return null; yield return null;
            onReady(ctrl);
        }

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

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
    /// 【開発用・エディタ限定】issue #270（廃止済みの選手個人の総合ランクの掃除）の受け入れスクショ。
    /// スタメン決定画面（打順行・控え行・先発投手ピル・比較カード）と対戦カード画面を撮る。使い捨て。
    /// </summary>
    public sealed class Issue270Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/i270";
        private const int PrefectureId = 13;   // 神奈川（HomeState と同じ）
        private const int W = 1600;
        private const int H = 900;

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // ScreenRouter は起動フレームで HomeDashboard を Show するので、落ち着くまで待ってから奪う。
            for (var i = 0; i < 10; i++) yield return null;

            SetUpTournament();

            // ── スタメン決定画面 ──
            MonoBehaviour lineup = null;
            yield return Focus("LineupSetting", r => lineup = r);
            if (lineup == null) { Debug.LogError("[i270] LineupSetting が見つからない"); yield break; }

            // 左＝選択、右＝ホバーで比較カードA/Bを両方出す（カテゴリ別ランクの置換先を確認）。
            Select(lineup, picked: 0, hovered: 3, tab: 0);
            yield return Capture(lineup, "01-lineup-compare");

            // DH制ON＝打順外の先発投手ピル（ランクチップを撤去した対象）が出る。
            ToggleDh(lineup);
            Select(lineup, picked: 0, hovered: 3, tab: 0);
            yield return Capture(lineup, "02-lineup-dh");

            // 投手能力タブ（比較カードは同じ・行だけ切替）。
            Select(lineup, picked: 0, hovered: 3, tab: 1);
            yield return Capture(lineup, "03-lineup-pitch-tab");

            // ── 対戦カード画面 ──
            SetUpLineup(useDh: false);
            MonoBehaviour mp = null;
            yield return Focus("MatchPreview", r => mp = r);
            if (mp == null) { Debug.LogError("[i270] MatchPreview が見つからない"); yield break; }
            yield return Capture(mp, "04-matchpreview");

            SetUpLineup(useDh: true);
            mp.gameObject.SetActive(false);
            yield return null;
            mp.gameObject.SetActive(true);
            yield return null; yield return null;
            yield return Capture(mp, "05-matchpreview-dh");

            // 参考：既存のメンバー設定の比較カード（同じ CategoryRankChips・同じ384px幅）と見た目を突き合わせる。
            MonoBehaviour member = null;
            yield return Focus("MemberSetting", r => member = r);
            if (member != null)
            {
                Select(member, picked: 0, hovered: 3, tab: 0);
                yield return Capture(member, "06-member-compare-ref");
            }

            Debug.Log("[i270] DONE");
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
                Debug.Log("[i270] wrote " + file);
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

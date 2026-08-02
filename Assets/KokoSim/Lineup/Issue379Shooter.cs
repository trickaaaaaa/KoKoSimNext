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
    /// 【開発用・エディタ限定】issue #379 の受け入れスクショ。触った2画面だけを撮る。
    ///  ① 大会展望の注目選手カード＝背番号バッジの投打色分けを廃止（中立面色へ）
    ///  ② スタメン設定の比較カード＝成績列を出場実績ベースへ（非DHの投手は打席も立つので投打が両方出る）
    /// 使い捨て。確認後に削除してよい。
    /// </summary>
    public sealed class Issue379Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/i379";
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

            // ── ① 大会展望：注目選手カード ──
            MonoBehaviour tp = null;
            yield return Focus("TournamentPreview", r => tp = r);
            if (tp == null) { Debug.LogError("[i379] TournamentPreview が見つからない"); yield break; }

            var showPreview = tp.GetType().GetMethod("ShowPreviewView");
            if (showPreview == null) { Debug.LogError("[i379] ShowPreviewView が無い"); yield break; }
            showPreview.Invoke(tp, new object[] { true });
            yield return null; yield return null;

            // 注目選手ブロックまでスクロールしてから撮る（カードが画面内に来ないと色が確認できない）。
            var scroll = tp.GetComponent<UIDocument>().rootVisualElement.Q<ScrollView>(className: "scroll");
            if (scroll != null) scroll.scrollOffset = new Vector2(0, 900);
            yield return null; yield return null;
            yield return Capture(tp, "01-preview-notables");

            // ── ② スタメン設定：比較カードの成績列 ──
            // 自校の試合を数試合こなして成績を作る。非DHなので投手は打席にも立つ＝投打の両方が溜まる。
            // InTournament を先に見る＝自校が敗退すると ExitTournamentIfFinished が Runner を畳んで
            // null にするので、Runner.Finished を直に見ると NRE で落ちる。
            for (var i = 0; i < 40 && GameSession.Current.InTournament && !GameSession.Current.Runner.Finished; i++)
            {
                if (GameSession.Current.ReachedMatchDay) { GameSession.Current.PlayMatch(); GameSession.Current.ConsumeResult(); }
                else GameSession.Current.AdvanceDay();
                yield return null;
            }
            Debug.Log("[i379] 成績を持つ選手数 career=" + GameSession.Current.Stats.Career.Players.Count);

            MonoBehaviour lineup = null;
            yield return Focus("LineupSetting", r => lineup = r);
            if (lineup == null) { Debug.LogError("[i379] LineupSetting が見つからない"); yield break; }

            // 左＝投打の両方に出場実績がある選手（＝二刀流の見え方）、右＝打撃だけの野手。
            Select(lineup, picked: TwoWayRow(lineup), hovered: 0, tab: 0);
            yield return Capture(lineup, "02-lineup-stats");

            // 成績列（#lu-cmp-stats）は比較カードの下端にあり既定の画角に入らない。そこまで送ってから撮る。
            var root = lineup.GetComponent<UIDocument>().rootVisualElement;
            var statHost = root.Q<VisualElement>("lu-cmp-stats");
            if (statHost != null)
            {
                for (var e = statHost.parent; e != null; e = e.parent)
                    if (e is ScrollView sv) { sv.ScrollTo(statHost); break; }
                yield return null; yield return null;
                yield return Capture(lineup, "03-lineup-stats-scrolled");
            }
            else Debug.LogError("[i379] lu-cmp-stats が見つからない");

            Debug.Log("[i379] DONE");
        }

        // 投打の両方に出場実績がある選手（対戦打者数が最大）の名簿添字。取れなければ 0。
        // 打順9番＝投手枠で採ると、背番号1の選手と実際に投げた選手が別人のときに外す。
        private static int TwoWayRow(MonoBehaviour ctrl)
        {
            var state = ctrl.GetType().GetField("_state", Priv)?.GetValue(ctrl);
            var roster = state?.GetType().GetField("_roster", Priv)?.GetValue(state)
                as IReadOnlyList<KokoSim.Engine.Season.DevelopingPlayer>;
            if (roster == null) return 0;

            var book = GameSession.Current.Stats.Career;
            var best = -1;
            var bestBf = 0;
            for (var i = 0; i < roster.Count; i++)
            {
                var s = book.Get(roster[i].Id);
                if (s == null || s.Batting.PlateAppearances == 0) continue;
                if (s.Pitching.BattersFaced <= bestBf) continue;
                bestBf = s.Pitching.BattersFaced;
                best = i;
            }
            Debug.Log("[i379] 二刀流の対象 row=" + best + " 対戦打者=" + bestBf);
            return best >= 0 ? best : 0;
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
                Debug.Log("[i379] wrote " + file);
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

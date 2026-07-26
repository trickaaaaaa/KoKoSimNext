#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Lineup
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #270 後追い（内部の並び順を用途別のキーへ置換）の受け入れ。
    /// 控え一覧・ブルペン/ベンチ・メンバー設定プール・個別指導枠の並びを実データで書き出しつつ、
    /// 該当画面のスクショを撮る。使い捨て。
    /// </summary>
    public sealed class Issue270OrderShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/i270b";
        private const int PrefectureId = 13;   // 神奈川（HomeState と同じ）
        private const int W = 1600;
        private const int H = 900;

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly TeamStrengthCoefficients Coeff = TeamStrengthCoeff.Default;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 10; i++) yield return null;

            SetUpTournament();   // LineupSetting は大会モードでないと中身が出ない
            DumpOrders();

            MonoBehaviour lineup = null;
            yield return Focus("LineupSetting", r => lineup = r);
            if (lineup != null)
            {
                Select(lineup, picked: 0, hovered: 3, tab: 0);
                yield return Capture(lineup, "01-lineup-bench");
            }
            else Debug.LogError("[i270b] LineupSetting が見つからない");

            MonoBehaviour member = null;
            yield return Focus("MemberSetting", r => member = r);
            if (member != null)
            {
                Select(member, picked: -1, hovered: -1, tab: 0);
                yield return Capture(member, "02-member-pool");
            }
            else Debug.LogError("[i270b] MemberSetting が見つからない");

            MonoBehaviour home = null;
            yield return Focus("HomeDashboard", r => home = r);
            if (home != null) yield return Capture(home, "03-home-guidance");
            else Debug.LogError("[i270b] HomeDashboard が見つからない");

            Debug.Log("[i270b] DONE");
        }

        /// <summary>並びが用途別のキーになっているかを、実データの数値つきでコンソールへ出す。</summary>
        private static void DumpOrders()
        {
            var roster = RosterService.Active;
            var state = new LineupSettingState();
            var spec = state.ToLineupSpec();

            var sb = new StringBuilder();
            sb.AppendLine("[i270b] ブルペン＝継投順（期待＝背番号昇順）");
            foreach (var p in spec.Bullpen) sb.AppendLine("  " + Line(p));
            sb.AppendLine("[i270b] ベンチ＝代打順（期待＝背番号昇順）");
            foreach (var p in spec.Bench) sb.AppendLine("  " + Line(p));

            sb.AppendLine("[i270b] メンバー設定プール＝ベンチ外（期待＝打撃力降順）");
            // 画面が実際に出す並び（MemberSettingState.BuildView の Pool）をそのまま読む。
            var pool = new Member.MemberSettingState().BuildView().Pool;
            foreach (var row in pool) sb.AppendLine("  " + Line(roster[row.Index]));

            sb.AppendLine("[i270b] 個別指導の上位2名（期待＝打撃力の上位2名）");
            var byBatting = new List<DevelopingPlayer>(roster);
            byBatting.Sort((a, b) => Str(b).Batting.CompareTo(Str(a).Batting));
            for (var i = 0; i < 2 && i < byBatting.Count; i++) sb.AppendLine("  " + Line(byBatting[i]));

            Debug.Log(sb.ToString());
        }

        private static void SetUpTournament()
        {
            var manager = new School
            {
                Id = -1,
                Name = NationService.ManagerSchoolName,
                PrefectureId = PrefectureId,
                Strength = TeamOverall.Of(RosterService.Active),
            };

            var coeff = new NationCoefficients();
            var nation = NationGenerator.Generate(
                SchoolNameVocabProvider.Default, coeff, new KokoSim.Engine.Core.Xoshiro256Random(2026));

            var field = new List<School> { manager };
            foreach (var s in nation.InPrefecture(PrefectureId))
                if (s.Id != manager.Id) field.Add(s);

            var runner = new KokoSim.Engine.Nation.Tournaments.TournamentRunner(
                field, manager, coeff, new KokoSim.Engine.Core.Xoshiro256Random(4242),
                new KokoSim.Engine.Nation.Tournaments.TournamentSchedule(),
                "2027年 夏の神奈川県大会", new PlayerMatchResolver());

            GameSession.Current.Year = 1;
            GameSession.Current.EnterTournament(
                TournamentKind.Summer, "2027年 夏の神奈川県大会", runner, field);
            GameSession.Current.ConsumeBanner();
        }

        private static PlayerStrength Str(DevelopingPlayer p) => PlayerStrengthProfile.Compute(p, Coeff);

        private static string Line(DevelopingPlayer p)
        {
            var s = Str(p);
            return string.Format("{0,-10} 背{1,-3} 投手={2} 打撃力={3:0.0} 投手力={4:0.0}",
                p.Name, p.UniformNumber, p.IsPitcher ? "○" : "－", s.Batting, s.Pitching);
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
                Debug.Log("[i270b] wrote " + file);
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

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Lineup
{
    /// <summary>【開発用・エディタ限定】issue #870：スタメン画面の怪我チップ（あとN週／あとN日）の確認撮影。出力は screenshots/issue-870/。</summary>
    public sealed class Issue870Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/issue-870";
        private const int PrefectureId = 13;
        private const int W = 1600;
        private const int H = 900;

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 10; i++) yield return null;

            Injure();

            MonoBehaviour lineup = null;
            yield return Focus("LineupSetting", r => lineup = r);
            if (lineup == null) { Debug.LogError("[870] LineupSetting が見つからない"); yield break; }
            var state = lineup.GetType().GetField("_state", Priv)?.GetValue(lineup) as LineupSettingState;
            state.ToggleDh();
            Render(lineup);
            yield return Capture(lineup, "01-week");

            SetUpTournament();
            GameSession.Current.AdvanceDay();
            GameSession.Current.AdvanceDay();
            GameSession.Current.AdvanceDay();
            Debug.Log("[870] InTournament=" + GameSession.Current.InTournament + " day=" + GameSession.Current.TournamentDay);
            Render(lineup);
            yield return Capture(lineup, "02-tournament");
            Debug.Log("[870] DONE");
        }

        private static void Render(MonoBehaviour ctrl) => ctrl.GetType().GetMethod("Render", Priv)?.Invoke(ctrl, null);

        private static void Injure()
        {
            var k = 0;
            foreach (var p in RosterService.Active)
            {
                if (k >= 14) break;
                var sev = k % 3 == 0 ? InjurySeverity.Severe : k % 3 == 1 ? InjurySeverity.Moderate : InjurySeverity.Minor;
                var type = k % 2 == 0 ? InjuryType.Sprain : InjuryType.Fracture;
                InjuryModel.Apply(p, new InjuryDiagnosis(type, InjurySite.Ankle, sev,
                    InjuryModel.RecoveryWeeks(sev, new InjuryCoefficients(), 1.0)));
                k++;
            }
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
                Debug.Log("[order] wrote " + file);
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

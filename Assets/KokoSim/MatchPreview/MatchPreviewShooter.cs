#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Unity.Lineup;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.MatchPreview
{
    /// <summary>
    /// 【開発用・エディタ限定】試合開始前（対戦カード）画面の受け入れスクショ（issue #7）。
    /// 大会モードを組み立て → スタメンを確定 → 対戦カード画面を撮る。DH制のときの
    /// 「打順外の先発投手」行も撮る。使い捨て。確認後に削除してよい。
    /// </summary>
    public sealed class MatchPreviewShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/match-preview";
        private const int PrefectureId = 13;   // 神奈川（HomeState と同じ）
        private const int ManagerSchoolId = -1;
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            SetUpTournament();
            SetUpLineup(useDh: false);

            EnsureOnly("MatchPreview");
            yield return null; yield return null;
            EnsureOnly("MatchPreview");
            var doc = FindDoc("MatchPreview");
            if (doc == null) { Debug.LogError("[MP] MatchPreview が見つからない"); yield break; }
            yield return null; yield return null;

            if (doc.rootVisualElement.Q<Button>("mp-start") == null)
                Debug.LogError("[MP] 試合開始ボタンが UXML に無い");
            yield return Capture(doc, "00-matchup");

            // DH制（打順外の先発投手行が出る）。
            SetUpLineup(useDh: true);
            doc.gameObject.SetActive(false);
            yield return null;
            doc.gameObject.SetActive(true);
            yield return null; yield return null;
            yield return Capture(doc, "01-matchup-dh");

            Debug.Log("[MP] DONE all shots");
        }

        private static void SetUpTournament()
        {
            var roster = RosterService.Roster;
            var manager = new School
            {
                Id = ManagerSchoolId,
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

        // スタメン設定画面と同じ入口で打順を確定する（実試合と同じラインナップになる）。
        private static void SetUpLineup(bool useDh)
        {
            var state = new LineupSettingState();
            if (useDh) state.ToggleDh();
            GameSession.Current.Lineup = state.ToLineupSpec();
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
                Debug.Log("[MP] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        private static void EnsureOnly(string screen)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == screen);
            }
        }

        private static UIDocument FindDoc(string screen)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) return d;
            }
            return null;
        }
    }
}
#endif

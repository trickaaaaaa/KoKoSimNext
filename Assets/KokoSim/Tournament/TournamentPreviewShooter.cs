#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 【開発用・エディタ限定】大会展望画面の受け入れスクショ。大会モードを組み立ててから
    /// トーナメントビュー → 大会展望ビュー（構図／注目選手／登録メンバー）を1セッションで撮る。
    /// 使い捨て。確認後に削除してよい。
    /// </summary>
    public sealed class TournamentPreviewShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/tournament-preview";
        private const int PrefectureId = 13;   // 神奈川（HomeState と同じ）
        private const int ManagerSchoolId = -1;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // ── 1. 非大会時の空状態 ──
            EnsureOnly("TournamentPreview");
            yield return null; yield return null;
            EnsureOnly("TournamentPreview");
            var doc = FindDoc("TournamentPreview");
            if (doc == null) { Debug.LogError("[TP] TournamentPreview が見つからない"); yield break; }
            yield return null;
            yield return Capture(doc, 1600, 900, "00-empty-state");

            // ── 2. 大会モードへ入る（HomeState と同じ組み立て） ──
            SetUpTournament();
            // 何試合か消化して経過を作る。
            for (var i = 0; i < 3 && !GameSession.Current.Runner.Finished; i++)
            {
                GameSession.Current.PlayMatch();
                GameSession.Current.ConsumeResult();
            }

            // 画面を組み直す（OnEnable を再実行させる）。
            doc.gameObject.SetActive(false);
            yield return null;
            doc.gameObject.SetActive(true);
            yield return null; yield return null;

            // ── 3. トーナメントビュー ──
            yield return Capture(doc, 1600, 900, "01-bracket");

            // 樹形図ブロック（自校カードへ自動スクロール済みの状態）を単体で撮る。
            var page = doc.rootVisualElement.Q<ScrollView>(className: "scroll");
            if (page != null) page.scrollOffset = new Vector2(0, 260);
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "01b-bracket-tree");
            if (page != null) page.scrollOffset = Vector2.zero;
            yield return null;

            // ── 4. 大会展望ビューへ（CTA と同じ遷移をAPIで確実に起こす） ──
            var ctrl = doc.GetComponent<TournamentPreviewController>();
            if (ctrl == null) { Debug.LogError("[TP] Controller が見つからない"); yield break; }
            if (doc.rootVisualElement.Q<Button>("tp-go-preview") == null)
                Debug.LogError("[TP] CTA ボタンが UXML に無い");
            ctrl.ShowPreviewView(true);
            yield return null;

            var scroll = doc.rootVisualElement.Q<ScrollView>();
            if (scroll != null) { scroll.scrollOffset = Vector2.zero; }
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "02-preview-top");

            // 注目選手あたりまでスクロール。
            if (scroll != null) scroll.scrollOffset = new Vector2(0, 900);
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "03-preview-notables");

            if (scroll != null) scroll.scrollOffset = new Vector2(0, 1750);
            yield return null; yield return null;
            yield return Capture(doc, 1600, 900, "04-preview-roster");

            Debug.Log("[TP] DONE all shots");
        }

        private static void SetUpTournament()
        {
            var roster = RosterService.Roster;
            var strength = TeamOverall.Of(roster);
            var manager = new School
            {
                Id = ManagerSchoolId, Name = "桜丘", PrefectureId = PrefectureId, Strength = strength,
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
                "2027年 秋季神奈川県大会", new PlayerMatchResolver());

            GameSession.Current.Year = 1;
            GameSession.Current.EnterTournament(
                TournamentKind.Autumn, "2027年 秋季神奈川県大会", runner, field);
            GameSession.Current.ConsumeBanner();
        }

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
                Debug.Log("[TP] wrote " + file);
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

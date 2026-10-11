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
    /// 【開発用・エディタ限定】issue #645 の受け入れスクショ。新聞型トーナメント表を
    /// 県大会・甲子園それぞれ「序盤（未消化）・中盤・優勝決定後」の3時点で撮り、同じ時点の大会パンフレット HTML を
    /// screenshots/pamphlet/ へ書き出す。最後に展望ビュー（HTMLで開くボタン）を撮る。使い捨て。
    /// </summary>
    public sealed class PamphletBracketShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/pamphlet";
        private const int PrefectureId = 13;
        private const int ManagerSchoolId = -1;
        private static List<School> _field;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            EnsureOnly("TournamentPreview");
            yield return null; yield return null;
            EnsureOnly("TournamentPreview");
            var doc = FindDoc("TournamentPreview");
            if (doc == null) { Debug.LogError("[PB] TournamentPreview が見つからない"); yield break; }
            var ctrl = doc.GetComponent<TournamentPreviewController>();

            foreach (var koshien in new[] { false, true })
            {
                var tag = koshien ? "koshien" : "pref";
                var runner = SetUp(koshien);
                for (var phase = 0; phase < 3; phase++)
                {
                    if (phase == 1)
                        for (var i = 0; i < 3 && !runner.Finished; i++) { GameSession.Current.PlayMatch(); GameSession.Current.ConsumeResult(); }
                    if (phase == 2)
                    {
                        while (!runner.Finished) { GameSession.Current.PlayMatch(); GameSession.Current.ConsumeResult(); }
                        runner.DrainBracketResolution();
                        // 自校の大会は終わった瞬間に対話モードを抜ける（＝画面から消える）。優勝決定後の表は
                        // 「観るだけの甲子園」と同じ枠（終わっても表示が残る）に据えて描かせる。
                        GameSession.Current.EnterSpectatedKoshien(runner.BuildBracketView().Title, runner, _field);
                    }
                    var name = tag + "-" + phase + (phase == 0 ? "-early" : phase == 1 ? "-mid" : "-final");

                    yield return Reopen(doc);
                    ctrl.ShowPreviewView(false);
                    yield return null;
                    var page = doc.rootVisualElement.Q<ScrollView>(className: "scroll");
                    var brk = doc.rootVisualElement.Q<ScrollView>("tp-bracket-scroll");
                    if (page != null && brk != null) page.scrollOffset = new Vector2(0, Mathf.Max(0, brk.layout.y - 60));
                    yield return null; yield return null;
                    yield return Capture(doc, 1600, 900, name);   // 初期スクロール＝自校の最新の試合

                    if (brk != null)
                    {
                        brk.horizontalScroller.value = brk.horizontalScroller.highValue * 0.5f;
                        brk.verticalScroller.value = brk.verticalScroller.highValue * 0.5f;
                    }
                    yield return null; yield return null;
                    yield return Capture(doc, 1600, 900, name + "-center");

                    WriteHtml(name);
                }

                if (koshien)
                {
                    ctrl.ShowPreviewView(true);
                    yield return null;
                    var page = doc.rootVisualElement.Q<ScrollView>(className: "scroll");
                    if (page != null) page.scrollOffset = Vector2.zero;
                    yield return null; yield return null;
                    yield return Capture(doc, 1600, 900, "preview-button");
                    // 本番の書き出し経路（persistentDataPath＋OpenURL）を1回だけ通す。
                    var export = typeof(TournamentPreviewController).GetMethod("ExportPamphlet",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    export.Invoke(ctrl, null);
                    Debug.Log("[PB] exported to " + Path.Combine(Application.persistentDataPath, "previews"));
                }
            }
            Debug.Log("[PB] DONE");
        }

        private static void WriteHtml(string name)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var v = new TournamentPreviewState().Build();
            var bracket = NewspaperBracketLayout.Build(GameSession.Current.Display.Runner.BuildBracketView(), TournamentPamphletHtml.Metrics);
            var schools = TournamentPreviewState.BuildPamphletSchools(v, bracket);
            var html = TournamentPamphletHtml.Render(v, bracket, schools);
            File.WriteAllText(Path.Combine(Dir, name + ".html"), html, new System.Text.UTF8Encoding(false));
            Debug.Log("[PB] html " + name + " schools=" + schools.Count + " " + sw.ElapsedMilliseconds + "ms");
        }

        private static TournamentRunner SetUp(bool koshien)
        {
            GameSession.Current.ResetForNewGame();
            // 自校を圧倒的に強くして最後まで勝ち進ませる（負けると大会表示ごと閉じ、中盤・優勝後が撮れない）。
            foreach (var p in RosterService.Roster)
            {
                foreach (var k in AbilityKinds.All) { p.SetCap(k, 99); p.SetLevel(k, 95); }
                p.Mental = 95;
                p.ConditionValue = 1.0;
            }
            var coeff = new NationCoefficients();
            var nation = NationGenerator.Generate(SchoolNameVocabProvider.Default, coeff, new Xoshiro256Random(2026));
            // 自校は強めにして経路を伸ばす（中盤で自校の太線が見えるように）。
            var manager = new School { Id = ManagerSchoolId, Name = "桜丘", PrefectureId = PrefectureId, Strength = 92 };
            var field = new List<School> { manager };
            if (koshien)
            {
                foreach (var pref in nation.Prefectures)
                {
                    if (pref.Id == PrefectureId) continue;
                    School best = null;
                    foreach (var s in nation.InPrefecture(pref.Id))
                        if (best == null || s.Strength > best.Strength) best = s;
                    if (best != null) field.Add(best);
                    if (field.Count >= 49) break;
                }
            }
            else
            {
                foreach (var s in nation.InPrefecture(PrefectureId)) field.Add(s);
            }
            var title = koshien ? "2028年 全国高等学校野球選手権大会" : "2028年 夏の神奈川県大会";
            var runner = new TournamentRunner(field, manager, coeff, new Xoshiro256Random(4242), new TournamentSchedule(),
                title, new PlayerMatchResolver(), isNationalTournament: koshien);
            GameSession.Current.Year = 1;
            _field = field;
            GameSession.Current.EnterTournament(koshien ? TournamentKind.SummerKoshien : TournamentKind.Summer, title, runner, field);
            GameSession.Current.ConsumeBanner();
            return runner;
        }

        private static IEnumerator Reopen(UIDocument doc)
        {
            doc.gameObject.SetActive(false);
            yield return null;
            doc.gameObject.SetActive(true);
            yield return null; yield return null;
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
                Debug.Log("[PB] wrote " + file);
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
                if (d.gameObject.scene.IsValid() && d.gameObject.name == name) return d;
            return null;
        }
    }
}
#endif

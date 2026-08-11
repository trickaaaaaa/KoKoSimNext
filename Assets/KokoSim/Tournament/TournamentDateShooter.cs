#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 【開発用・エディタ限定】大会日程の実月日表示（issue #583）の受け入れスクショ。
    /// 夏の大会へ入って日を送り、トップバーの小書きと次戦カードが実月日で出ることを確かめる。使い捨て。
    /// </summary>
    public sealed class TournamentDateShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/tournament-date";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var home = Open("HomeDashboard");
            if (home == null) { Debug.LogError("[TD] HomeDashboard が見つからない"); yield break; }

            // 夏の大会開幕週まで週を送る（EnterWeek が大会モードへ入れる）。
            var guard = 0;
            while (!GameSession.Current.InTournament && guard++ < 40)
            {
                GameClock.Advance(1);
                yield return null;
            }
            if (!GameSession.Current.InTournament) { Debug.LogError("[TD] 大会へ入れなかった"); yield break; }

            var s = GameSession.Current;
            Debug.Log("[TD] 大会入り title=" + s.Title + " kind=" + s.Kind
                      + " day=" + s.TournamentDay
                      + " 実月日=" + TournamentDate.Label(TournamentEntry.Schedule, s.Kind, s.TournamentDay)
                      + " 次戦=" + (s.Runner != null ? s.Runner.NextMatchDay.ToString() : "-"));

            home = Open("HomeDashboard");
            yield return null; yield return null;
            yield return Capture(home, "00-opening-day");

            // 数日送って日付が実月日で進むことを見る（休養日も跨ぐ）。
            for (var d = 0; d < 4; d++)
            {
                GameSession.Current.AdvanceDay();
                yield return null;
            }
            Debug.Log("[TD] day=" + GameSession.Current.TournamentDay
                      + " 実月日=" + TournamentDate.Label(
                          TournamentEntry.Schedule, GameSession.Current.Kind, GameSession.Current.TournamentDay)
                      + " 次戦まで=" + GameSession.Current.DaysUntilNextMatch + "日");

            home = Open("HomeDashboard");
            yield return null; yield return null;
            yield return Capture(home, "01-days-passed");

            // 大会画面（次戦の実月日つき）。
            var preview = Open("TournamentPreview");
            if (preview != null)
            {
                yield return null; yield return null;
                yield return Capture(preview, "02-tournament-screen");
            }

            Debug.Log("[TD] DONE all shots");
        }

        private static UIDocument Open(string screen)
        {
            UIDocument target = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) target = d;
                d.gameObject.SetActive(false);
            }
            if (target != null) target.gameObject.SetActive(true);
            return target;
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
                Debug.Log("[TD] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }
    }
}
#endif

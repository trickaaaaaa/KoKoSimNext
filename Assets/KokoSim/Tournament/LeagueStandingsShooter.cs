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
    /// 【開発用・エディタ限定】大会タブのブロック順位表（秋季地区予選のリーグ戦・issue #772）を撮る。
    /// 神奈川の秋季地区予選へ直接入り、開幕直後・1試合後・全節の直前の3枚を書き出す。
    /// </summary>
    public sealed class LeagueStandingsShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/league-standings";

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            EnsureOnly("TournamentPreview");
            yield return null; yield return null;
            var doc = FindDoc("TournamentPreview");
            if (doc == null) { Debug.LogError("[LG] TournamentPreview が見つからない"); yield break; }

            TournamentEntry.Enter(TournamentKind.Autumn);   // 神奈川＝地区予選（リーグ戦）から入る
            GameSession.Current.ConsumeBanner();
            Debug.Log("[LG] kind=" + GameSession.Current.Kind + " runner=" + GameSession.Current.Runner?.GetType().Name);

            yield return Reopen(doc);
            yield return Capture(doc, 1600, 900, "00-start");

            for (var i = 0; i < 2 && GameSession.Current.InTournament && !GameSession.Current.Runner.Finished; i++)
            {
                GameSession.Current.PlayMatch();
                GameSession.Current.ConsumeResult();
                yield return Reopen(doc);
                yield return Capture(doc, 1600, 900, "0" + (i + 1) + "-after-" + (i + 1));
            }
            var page = doc.rootVisualElement.Q<ScrollView>(className: "scroll");
            if (page != null) page.scrollOffset = new Vector2(0, 420);
            yield return Capture(doc, 1600, 900, "03-scrolled");
            Debug.Log("[LG] DONE");
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
                Debug.Log("[LG] wrote " + file);
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

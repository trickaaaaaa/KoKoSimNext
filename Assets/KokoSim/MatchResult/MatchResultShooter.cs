#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.MatchResult
{
    /// <summary>
    /// 【開発用・エディタ限定】試合結果画面の受け入れスクショ（issue #13）。
    /// 実エンジンで1試合を消化し、その <see cref="GameResult"/> をそのまま結果画面へ流して撮る。
    /// 9回で終わる試合と延長（イニング列が伸びる）試合の2枚。使い捨て。確認後に削除してよい。
    /// </summary>
    public sealed class MatchResultShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/match-result";
        private const string AwayName = "北都大付属";
        private const string HomeName = "桜丘";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            EnsureOnly("MatchResult");
            yield return null; yield return null;
            EnsureOnly("MatchResult");
            var doc = FindDoc("MatchResult");
            if (doc == null) { Debug.LogError("[MR] MatchResult が見つからない"); yield break; }
            var ctrl = doc.GetComponent<MatchResultController>();
            if (ctrl == null) { Debug.LogError("[MR] MatchResultController が付いていない"); yield break; }
            if (doc.rootVisualElement.Q<Button>("mr-close") == null)
                Debug.LogError("[MR] 閉じるボタンが UXML に無い");

            // 勝ち試合（勝敗表記がアンバーになる）／負け試合／延長（イニング列が10回以降まで伸びる）。
            yield return Shot(doc, ctrl, "00-result-win", Kind.Win);
            yield return Shot(doc, ctrl, "01-result-lose", Kind.Lose);
            yield return Shot(doc, ctrl, "02-result-extra", Kind.Extra);

            Debug.Log("[MR] DONE all shots");
        }

        private enum Kind { Win, Lose, Extra }

        // 条件に合う試合を1つ探して撮る（自校＝後攻 home 固定。実フローと同じ向き）。
        private IEnumerator Shot(UIDocument doc, MatchResultController ctrl, string file, Kind kind)
        {
            ulong seed;
            var r = FindGame(kind, out seed);
            if (r == null) { Debug.LogWarning("[MR] 条件の試合が見つからず: " + kind); yield break; }

            ctrl.RenderForCapture(r, managerIsAway: false, AwayName, HomeName);
            Debug.Log("[MR] " + kind + " seed=" + seed + " " + r.AwayRuns + "-" + r.HomeRuns
                + " " + r.HomeLineScore.Count + "回");
            yield return null; yield return null;
            yield return Capture(doc, file);
        }

        // 実エンジンで試合を消化して条件に合うものを探す（表示検証用。独立シード＝セッションの乱数列に触らない）。
        private static GameResult FindGame(Kind kind, out ulong usedSeed)
        {
            var home = RosterTeamBuilder.Build(RosterService.Active, HomeName);
            for (ulong seed = 2026; seed < 3026; seed++)
            {
                var away = StrengthTeamFactory.Create(58, AwayName, new Xoshiro256Random(seed ^ 0x1234ABCDUL));
                var r = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(seed));
                var ok = kind switch
                {
                    Kind.Win => r.HomeRuns > r.AwayRuns,
                    Kind.Lose => r.HomeRuns < r.AwayRuns,
                    _ => r.AwayLineScore.Count > 9,
                };
                if (ok) { usedSeed = seed; return r; }
            }
            usedSeed = 0;
            return null;
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
                Debug.Log("[MR] wrote " + file);
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

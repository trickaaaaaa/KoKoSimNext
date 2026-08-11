#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】トップバーのカウントダウン（issue #582）の受け入れスクショ。
    /// 夏前・夏の開幕週・夏後・秋の開幕週・秋後の5点で「次の公式戦」の見出しと残り週を撮る。
    /// ホーム（HomeState 経由＝4引数の Fill）と選手一覧（no-arg の Fill）の**両方の経路**を通す。使い捨て。
    /// </summary>
    public sealed class CountdownShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/countdown";
        private const int W = 1600;
        private const int H = 900;

        // 夏=第13週 / 秋=第23週（SeasonCalendar）。旧実装は第14〜49週がずっと「開催中」だった。
        private static readonly (int Week, string Tag)[] Weeks =
        {
            (5, "05-before-summer"),
            (13, "13-summer-live"),
            (16, "16-after-summer"),
            (23, "23-autumn-live"),
            (30, "30-after-autumn"),
        };

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // 最初の数フレームは UIDocument が組み上がっておらず、撮ると白紙＋起動時（第0週）の
            // 内容が写る。1枚目だけ壊れるので必ず待つ。
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            foreach (var t in Weeks)
            {
                GameClock.Reset();
                NewTeamService.Clear();
                GameClock.Advance(t.Week);
                // 引退週(17)以降は次期主将の指名待ちでホームがモーダルに覆われる。撮りたいのは
                // トップバーなので、待ちが立っていたら先頭候補で確定して素のホームへ戻す。
                if (NewTeamService.Pending) NewTeamService.Confirm(0);
                // 週送りで大会開幕週(13/23)を跨ぐと大会モードへ入り、以後 GameSession は戻らない
                // ＝ホームが「次戦まで N日」の大会ビューになる。本 issue で見たいのは**通常週**の
                // カウントダウンなので、進めた後に通常モードへ戻す（撮影用の細工）。
                GameSession.Current.ResetForNewGame();

                var next = GameClock.NextTournament;
                Debug.Log($"[CD] week={GameClock.Week} kind={next.Kind} left={next.WeeksLeft} "
                          + $"label={SeasonClock.CountdownLabel(next.Kind)}");

                var home = Open("HomeDashboard");
                if (home == null) { Debug.LogError("[CD] HomeDashboard が見つからない"); yield break; }
                yield return null; yield return null;
                yield return Capture(home, t.Tag + "-home");

                var list = Open("PlayerList");
                if (list == null) { Debug.LogError("[CD] PlayerList が見つからない"); yield break; }
                yield return null; yield return null;
                yield return Capture(list, t.Tag + "-players");
            }

            Debug.Log("[CD] DONE all shots");
        }

        // 指定画面だけをアクティブにして開き直す（OnEnable を確実に走らせる）。
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
                Debug.Log("[CD] wrote " + file);
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

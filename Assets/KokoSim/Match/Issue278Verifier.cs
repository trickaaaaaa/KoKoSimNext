#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #278（打席解決中のスキップで進行体を奪い合って NRE）の通し確認。
    /// 練習試合でライブ観戦へ入り、<see cref="BackgroundGameOp"/> のスロットを実際に塞いだ状態（＝打席解決中と
    /// 同じ条件）でスキップ・タイム・結果画面の「閉じる」を叩き、
    /// ①ボタンが伏せられている ②叩いても進行体を触らない ③解除後は従来どおり動く を検証する。使い捨て。
    /// </summary>
    public sealed class Issue278Verifier : MonoBehaviour
    {
        private const string Dir = "screenshots/i278";
        private const int W = 1600;
        private const int H = 900;

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private int _errors;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 10; i++) yield return null;

            // ── 練習試合でライブ観戦まで入る（#269 の経路をそのまま使う） ──
            MonoBehaviour practice = null;
            yield return Focus("PracticeMatch", r => practice = r);
            if (practice == null) { Fail("PracticeMatch が見つからない"); yield break; }
            if (!Request(practice)) { Fail("受諾される相手が見つからない"); yield break; }

            MonoBehaviour lineup = null;
            yield return WaitForScreen("LineupSetting", r => lineup = r);
            if (lineup == null) { Fail("スタメン設定へ遷移しない"); yield break; }
            Invoke(lineup, "OnOk");

            MonoBehaviour preview = null;
            yield return WaitForScreen("MatchPreview", r => preview = r);
            if (preview == null) { Fail("MatchPreview へ遷移しない"); yield break; }
            Invoke(preview, "OnStart");

            MonoBehaviour live = null;
            yield return WaitForScreen("MatchLive", r => live = r);
            if (live == null) { Fail("MatchLive へ遷移しない"); yield break; }
            for (var i = 0; i < 60; i++) yield return null;   // 数打席進ませる

            var careerBefore = CareerGames();

            // ── ① 解決中（＝BackgroundGameOp のスロットが塞がっている状態）を作る ──
            yield return WaitIdle();
            var progBefore = Field(live, "_prog");
            if (progBefore == null) { Fail("_prog が取れない"); yield break; }

            if (!BackgroundGameOp.Run(() => System.Threading.Thread.Sleep(1500), () => { }))
            { Fail("解決中の状態を作れない（スロットが空いていない）"); yield break; }

            Invoke(live, "UpdateControlState");
            yield return null;
            var skipOn = Enabled(live, "_skip");
            var timeOn = Enabled(live, "_time");
            Debug.Log("[i278] 解決中: スキップ活性=" + skipOn + " / タイム活性=" + timeOn + "（期待 どちらも False）");
            if (skipOn) Fail("解決中にスキップボタンが押せる");
            if (timeOn) Fail("解決中にタイムボタンが押せる");
            yield return Capture(live, "01-resolving-controls-disabled");

            // ② 解決中に叩いても進行体を触らない（旧コードはここで SkipDelegateToAi を走らせて NRE を撒いた）
            Invoke(live, "OnSkip");
            Invoke(live, "OnTime");
            yield return null;
            if ((bool)Field(live, "_gameOver")) Fail("解決中のスキップで試合が終局してしまった");
            if (!ReferenceEquals(progBefore, Field(live, "_prog"))) Fail("解決中のスキップで進行体が差し替わった");
            Debug.Log("[i278] 解決中にスキップ/タイムを叩いた → 終局せず進行体も無傷");

            // ③ 解除後は従来どおり操作できる
            yield return WaitIdle();
            Invoke(live, "UpdateControlState");
            yield return null;
            if (!Enabled(live, "_skip")) Fail("解決が終わってもスキップが伏せられたまま");
            Debug.Log("[i278] 解決終了: スキップ活性=" + Enabled(live, "_skip") + "（期待 True）");

            // ── ④ 通常のスキップ → 結果画面 ──
            yield return WaitIdle();
            Invoke(live, "OnSkip");
            yield return null; yield return null;

            MonoBehaviour result = null;
            yield return WaitForScreen("MatchResult", r => result = r);
            if (result == null) { Fail("MatchResult へ遷移しない"); yield break; }

            // ⑤ 結果画面の「閉じる」も、後処理スロットが塞がっている間は受け付けない
            //    （受けてしまうと預かった後処理ごと落ちたうえ遷移もしない）。
            if (!BackgroundGameOp.Run(() => System.Threading.Thread.Sleep(1200), () => { }))
            { Fail("閉じる検証用の占有ができない"); yield break; }
            Invoke(result, "OnClose");
            yield return null;
            if (Field(result, "_onClose") == null) Fail("処理中の「閉じる」で後処理コールバックが食われた");
            Debug.Log("[i278] 処理中の「閉じる」→ 後処理コールバックは保持されたまま");
            yield return Capture(result, "02-result-close-ignored-while-busy");

            // ⑥ 解除後に閉じれば、後処理（成績畳み込み）まで通る
            yield return WaitIdle();
            Invoke(result, "OnClose");
            yield return null;
            yield return WaitIdle();
            for (var i = 0; i < 10; i++) yield return null;

            var careerAfter = CareerGames();
            Debug.Log("[i278] 通算 " + careerBefore + "→" + careerAfter + "（後処理が走っていれば増える）");
            if (careerAfter <= careerBefore) Fail("「閉じる」の後処理が走っていない");

            MonoBehaviour home = null;
            yield return WaitForScreen("HomeDashboard", r => home = r);
            if (home == null) Fail("結果画面からホームへ戻らない");
            else yield return Capture(home, "03-home-after");

            Debug.Log(_errors == 0 ? "[i278] DONE 全項目パス" : "[i278] DONE 失敗 " + _errors + " 件");
        }

        private void Fail(string message) { _errors++; Debug.LogError("[i278] " + message); }

        private static bool Request(MonoBehaviour controller)
        {
            var state = controller.GetType().GetField("_state", Priv)?.GetValue(controller);
            if (state == null) return false;
            var select = state.GetType().GetMethod("Select");
            var request = state.GetType().GetMethod("Request");
            var ordered = new System.Collections.Generic.List<Engine.Nation.School>(NationService.PrefectureSchools);
            ordered.Sort((a, b) => a.Strength.CompareTo(b.Strength));   // 弱い相手ほど受諾されやすい
            foreach (var s in ordered)
            {
                select?.Invoke(state, new object[] { s.Id });
                request?.Invoke(state, null);
                Invoke(controller, "Render");
                if (GameSession.Current.PendingMatchIsPractice) return true;
            }
            return false;
        }

        private static int CareerGames()
        {
            var table = GameSession.Current.Stats.Career;
            var total = 0;
            foreach (var p in RosterService.Active)
            {
                var s = table.Get(p.Id);
                if (s?.Batting != null) total += s.Batting.Games;
            }
            return total;
        }

        private static object Field(MonoBehaviour mb, string name)
            => mb.GetType().GetField(name, Priv)?.GetValue(mb);

        private static bool Enabled(MonoBehaviour mb, string name)
            => Field(mb, name) is Button b && b.enabledSelf;

        private static void Invoke(MonoBehaviour mb, string method)
            => mb.GetType().GetMethod(method, Priv)?.Invoke(mb, null);

        private static IEnumerator WaitIdle()
        {
            for (var i = 0; i < 900 && BackgroundGameOp.Running; i++) yield return null;
            yield return null;
        }

        private static IEnumerator WaitForScreen(string screen, System.Action<MonoBehaviour> onReady)
        {
            MonoBehaviour found = null;
            for (var i = 0; i < 900 && found == null; i++)
            {
                foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                {
                    if (!d.gameObject.scene.IsValid() || !d.gameObject.activeInHierarchy) continue;
                    if (d.gameObject.name != screen) continue;
                    foreach (var mb in d.GetComponents<MonoBehaviour>())
                        if (mb != null && mb.GetType().Name.EndsWith("Controller")) found = mb;
                }
                if (found == null) yield return null;
            }
            yield return null;
            onReady(found);
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
                Debug.Log("[i278] wrote " + file);
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

#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Match.Game;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Practice
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #269（練習試合のライブ観戦化）の通し確認＋受け入れスクショ。
    /// 練習試合を申し込み → スタメン設定 → 対戦カード → MatchLive → 結果画面 → ホーム を実際に通し、
    /// 成績が通算にだけ積まれる（公式戦通算・今大会には入らない）ことも検証する。使い捨て。
    /// </summary>
    public sealed class Issue269Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/i269";
        private const int W = 1600;
        private const int H = 900;

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // ScreenRouter が起動フレームで HomeDashboard を Show するので落ち着くまで待つ。
            for (var i = 0; i < 10; i++) yield return null;

            // ── ① 練習試合画面で相手を選び、申し込む ──
            MonoBehaviour practice = null;
            yield return Focus("PracticeMatch", r => practice = r);
            if (practice == null) { Debug.LogError("[i269] PracticeMatch が見つからない"); yield break; }

            var state = practice.GetType().GetField("_state", Priv)?.GetValue(practice);
            if (state == null) { Debug.LogError("[i269] PracticeMatchState を取れない"); yield break; }

            // 自県の1校目を選ぶ（受諾されるまで相手を変える＝格下から順に当たる）。
            var funds0 = ManagerService.Manager.Funds;
            var picked = SelectAcceptableOpponent(state, practice);
            if (picked == null) { Debug.LogError("[i269] 受諾される相手が見つからない"); yield break; }
            yield return Capture(practice, "01-practice-requested");

            var session = GameSession.Current;
            if (!session.PendingMatchIsPractice)
            {
                Debug.LogError("[i269] 申込は成立したのに練習試合フローに入っていない");
                yield break;
            }
            if (!Mathf.Approximately((float)funds0, (float)ManagerService.Manager.Funds))
                Debug.LogError("[i269] 申込時点で費用が引かれている（ライブ開始時に確定するはず）");
            Debug.Log("[i269] 申込成立 vs " + picked + " / 資金 " + ManagerService.Manager.Funds + "万（未消費）");

            // ── ② スタメン設定 ──
            MonoBehaviour lineup = null;
            yield return WaitForScreen("LineupSetting", r => lineup = r);
            if (lineup == null) { Debug.LogError("[i269] スタメン設定へ遷移しない"); yield break; }
            yield return Capture(lineup, "02-lineup");

            var careerBefore = GamesOf(scope: 0);
            var officialBefore = GamesOf(scope: 1);
            var tournBefore = GamesOf(scope: 2);

            // 以降は実プレイと同じく「ルータに任せる」。画面を横取りして SetActive すると、
            // 一度きりの引き渡し（MatchLiveController.Pending 等）を空の OnEnable が食い潰す。
            Invoke(lineup, "OnOk");

            // ── ③ 対戦カード ──
            MonoBehaviour preview = null;
            yield return WaitForScreen("MatchPreview", r => preview = r);
            if (preview == null) { Debug.LogError("[i269] MatchPreview へ遷移しない"); yield break; }
            yield return Capture(preview, "03-matchpreview");

            // ── ④ 「試合開始」→ ホーム経由で MatchLive（費用と週枠はここで確定する） ──
            Invoke(preview, "OnStart");

            MonoBehaviour live = null;
            yield return WaitForScreen("MatchLive", r => live = r);
            if (live == null) { Debug.LogError("[i269] MatchLive へ遷移しない"); yield break; }

            var spent = funds0 - ManagerService.Manager.Funds;
            Debug.Log("[i269] ライブ開始で確定した費用 = " + spent + "万（期待 " + ManagerService.Practice.Cost + "）");
            for (var i = 0; i < 60; i++) yield return null;   // 数打席進ませてから撮る
            yield return Capture(live, "04-matchlive");

            // ── ⑤ 委任スキップで終局 → 結果画面 ──
            // 打席解決（AutoAdvanceNext）が背景で走っている最中に叩くと _prog を別スレッドと奪い合うので、
            // 実プレイと同じく「操作を受け付けられる状態」＝背景処理が空いてから叩く。
            yield return WaitIdle();
            Invoke(live, "OnSkip");
            yield return null; yield return null;

            MonoBehaviour result = null;
            yield return WaitForScreen("MatchResult", r => result = r);
            if (result == null) { Debug.LogError("[i269] MatchResult へ遷移しない"); yield break; }
            yield return Capture(result, "05-matchresult");

            // ── ⑥ 「閉じる」→ 後処理（通算だけ加算）→ ホーム ──
            yield return WaitIdle();          // 背景処理が空いてから（BackgroundGameOp は同時1本）
            Invoke(result, "OnClose");
            yield return null;
            yield return WaitIdle();          // 後処理（成績畳み込み・成長・怪我）の完了を待つ
            for (var i = 0; i < 10; i++) yield return null;

            var careerAfter = GamesOf(scope: 0);
            var officialAfter = GamesOf(scope: 1);
            var tournAfter = GamesOf(scope: 2);
            Debug.Log("[i269] 通算 " + careerBefore + "→" + careerAfter
                      + " / 公式戦 " + officialBefore + "→" + officialAfter
                      + " / 今大会 " + tournBefore + "→" + tournAfter);
            if (careerAfter <= careerBefore) Debug.LogError("[i269] 通算成績に積まれていない");
            if (officialAfter != officialBefore) Debug.LogError("[i269] 公式戦通算に積まれてしまった");
            if (tournAfter != tournBefore) Debug.LogError("[i269] 今大会に積まれてしまった");
            if (GameSession.Current.PendingMatchIsPractice) Debug.LogError("[i269] 練習試合フローが閉じていない");
            if (GameSession.Current.InTournament) Debug.LogError("[i269] 大会モードに入ってしまった");

            MonoBehaviour homeBack = null;
            yield return WaitForScreen("HomeDashboard", r => homeBack = r);
            if (homeBack == null) Debug.LogError("[i269] 結果画面からホームへ戻らない");
            else yield return Capture(homeBack, "06-home-after");

            // ── ⑦ 同じ週の2回目は週枠で塞がる ──
            MonoBehaviour practice2 = null;
            yield return Focus("PracticeMatch", r => practice2 = r);
            if (practice2 != null)
            {
                var st2 = practice2.GetType().GetField("_state", Priv)?.GetValue(practice2);
                st2?.GetType().GetMethod("Request")?.Invoke(st2, null);
                Invoke(practice2, "Render");
                yield return Capture(practice2, "07-practice-weekly-blocked");
                if (GameSession.Current.PendingMatchIsPractice)
                    Debug.LogError("[i269] 週枠消化済みなのに再びフローへ入った");
            }

            Debug.Log("[i269] DONE");
        }

        /// <summary>受諾されるまで相手を変えて申し込む。成立した相手校名を返す（見つからなければ null）。</summary>
        private static string SelectAcceptableOpponent(object state, MonoBehaviour controller)
        {
            var select = state.GetType().GetMethod("Select");
            var request = state.GetType().GetMethod("Request");
            var schools = NationService.PrefectureSchools;
            // 弱い相手ほど受諾されやすい（ティア差ペナルティ）。総合の低い順に当たる。
            var ordered = new System.Collections.Generic.List<Engine.Nation.School>(schools);
            ordered.Sort((a, b) => a.Strength.CompareTo(b.Strength));
            foreach (var s in ordered)
            {
                select?.Invoke(state, new object[] { s.Id });
                request?.Invoke(state, null);
                Invoke(controller, "Render");
                if (GameSession.Current.PendingMatchIsPractice) return s.Name;
            }
            return null;
        }

        /// <summary>成績スコープ別の「チーム合計試合数」（0=通算, 1=公式戦, 2=今大会）。</summary>
        private static int GamesOf(int scope)
        {
            var stats = GameSession.Current.Stats;
            var table = scope == 0 ? stats.Career : scope == 1 ? stats.Official : stats.CurrentTournament;
            var total = 0;
            foreach (var p in RosterService.Active)
            {
                var s = table.Get(p.Id);
                if (s?.Batting != null) total += s.Batting.Games;
            }
            return total;
        }

        private static void Invoke(MonoBehaviour mb, string method)
            => mb.GetType().GetMethod(method, Priv)?.Invoke(mb, null);

        /// <summary>背景処理（BackgroundGameOp は同時1本）が空くまで待つ。空かないまま叩くと Run が false を返す。</summary>
        private static IEnumerator WaitIdle()
        {
            for (var i = 0; i < 600 && BackgroundGameOp.Running; i++) yield return null;
            yield return null;
            if (BackgroundGameOp.Running) Debug.LogError("[i269] 背景処理が終わらない（待機タイムアウト）");
        }

        /// <summary>
        /// ルータが自力でその画面へ遷移するのを待つ（画面を横取りしない）。
        /// <see cref="Activate"/> で割り込むと、一度きりの引き渡し（<c>MatchLiveController.Pending</c> /
        /// <c>MatchResultController.Pending</c>）を空の OnEnable が食い潰して実試合がデモ試合にすり替わる。
        /// </summary>
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
                Debug.Log("[i269] wrote " + file);
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

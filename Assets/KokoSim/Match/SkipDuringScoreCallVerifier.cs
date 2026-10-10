#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】「得点の再生中にスキップ（委任）を押すと試合が終わらない」の通し確認
    /// （オーナー報告 2026-08-14）。
    ///
    /// <para><b>症状</b>: 内部では試合が終わっている（<c>_gameOver=True</c>）のに「ゲームセット」も
    /// 結果画面も来ない。投手が投げないだけに見え、UIは生きているので原因が画面に出ない。</para>
    ///
    /// <para><b>原因（当時）</b>: 得点コール（issue #438）は1点ずつの加算を打球再生の本塁到達だけで
    /// 進めていた。スキップはその再生を畳むので残り点が減らず <c>_scoreCallOn</c> が永久に立ち、
    /// Update の <c>if (_gameSetPending &amp;&amp; !_scoreCallOn)</c> が一生通らなかった
    /// （実測 <c>_scoreCallClock=696秒</c>）。issue #643 で加算演出を撤去し、得点コールは
    /// 最終スコアを掲げて実時間の最終ホールドで必ず消える形になった。</para>
    ///
    /// <para>ここで守る不変条件は「<b>得点コールの表示中にスキップしても「ゲームセット」→結果画面まで届く</b>」。
    /// 自然に得点コールへ当たるのを待ち、当たらなければ同じ状態を作って確かめる（どちらでも同じ経路を通る）。</para>
    /// </summary>
    public sealed class SkipDuringScoreCallVerifier : MonoBehaviour
    {
        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>タイトルから起動したときに読むセーブ。手元の検証用（保存はしないので書き換わらない）。</summary>
        private const string SlotId = "manual1";

        private int _errors;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            for (var i = 0; i < 10; i++) yield return null;

            // 部員が居ない＝タイトルから起動した。既存セーブを読んで試合できる状態にする
            // （保存は手動のみ＝読むだけならセーブは書き換わらない）。
            if (RosterService.Active.Count == 0)
            {
                string error;
                if (!SaveSlots.Load(SlotId, out error)) { Fail("セーブが読めない: " + error); yield break; }
                for (var i = 0; i < 10; i++) yield return null;
            }

            // ── 練習試合でライブ観戦まで入る（Issue278Verifier と同じ経路） ──
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

            // 倍速で回して得点の再生に当たる確率を上げる（観測だけ・結果には影響しない）。
            SetField(live, "_speed", 4f);
            for (var i = 0; i < 30; i++) yield return null;

            // ── 得点コールの表示中を捕まえる ──
            var natural = false;
            for (var i = 0; i < 4000 && !natural; i++)
            {
                if ((bool)Field(live, "_gameOver")) break;
                natural = (bool)Field(live, "_scoreCallOn");
                if (!natural) yield return null;
            }

            if ((bool)Field(live, "_gameOver")) { Fail("得点の再生を捕まえる前に試合が終わった（再実行で当たる）"); yield break; }

            if (!natural)
            {
                // 自然に当たらなかった場合は同じ状態を作る（守る不変条件は同一）。
                Debug.Log("[skip-score] 自然な得点再生を捕まえられなかったので状態を作って確かめる");
                SetField(live, "_scoreCallOn", true);
                SetField(live, "_scoreCallClock", 0f);
            }
            Debug.Log("[skip-score] 押下時: scoreCallOn=" + Field(live, "_scoreCallOn")
                      + " clock=" + Field(live, "_scoreCallClock")
                      + "（自然=" + natural + "）");

            // ── ここでスキップ（委任） ──
            yield return WaitIdle();
            Invoke(live, "OnSkip");
            yield return null;

            if (!(bool)Field(live, "_gameOver")) { Fail("スキップで終局しなかった"); yield break; }

            // ── 「ゲームセット」→ 結果画面まで到達すること ──
            var handedOff = false;
            for (var i = 0; i < 900 && !handedOff; i++)
            {
                handedOff = !(bool)Field(live, "_gameSetPending") && !(bool)Field(live, "_scoreCallOn");
                if (!handedOff) yield return null;
            }
            if (!handedOff)
            {
                Fail("得点コールが消えず「ゲームセット」に到達しない: scoreCallOn="
                     + Field(live, "_scoreCallOn") + " clock=" + Field(live, "_scoreCallClock"));
                yield break;
            }

            MonoBehaviour result = null;
            yield return WaitForScreen("MatchResult", r => result = r);
            if (result == null) Fail("結果画面へ遷移しない");

            Debug.Log(_errors == 0 ? "[skip-score] DONE 全項目パス" : "[skip-score] DONE 失敗 " + _errors + " 件");
        }

        private void Fail(string message) { _errors++; Debug.LogError("[skip-score] " + message); }

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

        private static object Field(MonoBehaviour mb, string name)
            => mb.GetType().GetField(name, Priv)?.GetValue(mb);

        private static void SetField(MonoBehaviour mb, string name, object value)
            => mb.GetType().GetField(name, Priv)?.SetValue(mb, value);

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
                if (d.gameObject.name != screen) continue;
                d.gameObject.SetActive(true);
                foreach (var mb in d.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.GetType().Name.EndsWith("Controller")) found = mb;
            }
            return found;
        }
    }
}
#endif

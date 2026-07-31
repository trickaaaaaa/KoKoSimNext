#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】イニング間のスコア画面が<b>実際の進行でも</b>出て、ちゃんと明けるかを見る計測。
    /// スクショ経路（ShowInningBreakForCapture）は見た目の確認しかできず、
    /// 「自動で明けずに進行が止まる」という一番痛い壊れ方を捕まえられないため。
    ///
    /// 攻守交代を数回またぐまで自動進行させ、開始/終了の回数と最後に進んだ回をログへ出す。使い捨て。
    /// </summary>
    public sealed class MatchLiveInningBreakProbe : MonoBehaviour
    {
        [SerializeField] private float watchSeconds = 60f;
        [SerializeField] private float speed = 4f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;   // 実進行（自動連続進行）で回す
            // 他画面のルーティングが後から走るので、シューターと同じく2度立てる。
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            MatchLiveController ctrl = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid() || d.gameObject.name != "MatchLive") continue;
                ctrl = d.GetComponent<MatchLiveController>();
            }
            if (ctrl == null) { Debug.LogError("[BreakProbe] MatchLive が見つからない"); yield break; }

            const BindingFlags Bf = BindingFlags.NonPublic | BindingFlags.Instance;
            var t = typeof(MatchLiveController);
            var fOn = t.GetField("_inningBreakOn", Bf);
            var fSpeed = t.GetField("_speed", Bf);
            var fCur = t.GetField("_current", Bf);
            fSpeed.SetValue(ctrl, speed);

            var began = 0;
            var ended = 0;
            var prevOn = false;
            var clock = 0f;
            var lastInning = 0;
            var lastTop = true;

            while (clock < watchSeconds)
            {
                clock += Time.deltaTime;
                var on = (bool)fOn.GetValue(ctrl);
                if (on && !prevOn) { began++; Debug.Log($"[BreakProbe] 開始 #{began} t={clock:F1}s"); }
                if (!on && prevOn) { ended++; Debug.Log($"[BreakProbe] 終了 #{ended} t={clock:F1}s"); }
                prevOn = on;

                var cur = fCur.GetValue(ctrl);
                if (cur is KokoSim.Engine.Match.Timeline.Playback.LivePlateAppearance pa)
                {
                    lastInning = pa.Inning;
                    lastTop = pa.IsTop;
                }
                if (ctrl.IsGameOver) break;
                yield return null;
            }

            var half = lastInning + (lastTop ? "回表" : "回裏");
            var verdict = began > 0 && ended == began ? "OK（出て・明けた）"
                : began == 0 ? "NG: 一度も出なかった"
                : "NG: 明けずに止まっている";
            Debug.Log($"[BreakProbe] 結果 {verdict} 開始={began} 終了={ended} 到達={half} gameOver={ctrl.IsGameOver}");
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }
    }
}
#endif

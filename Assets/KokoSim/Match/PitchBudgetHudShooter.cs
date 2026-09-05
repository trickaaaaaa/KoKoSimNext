#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Game;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】球数制限の可視化（out/issues-pending/pitch-limit-visibility.md）の受け入れスクショ。
    ///
    /// HUD投手帯の球数を「今 / 実効目安投球数」の分母つきにし、週間球数制限で先に止まる投手にだけ
    /// 「週残N球」チップを点ける変更を、<b>2状態とも1回の観戦で</b>撮る。
    /// <see cref="MatchLiveController.CaptureContextOverride"/> で上限を 200 球に落とすと、
    /// 序盤は残り200＞目安+マージン＝チップ消灯、中盤に残りが目安を割ってチップ点灯、と両方が出る。
    /// 上限を落とすのは撮影時だけ（既定 null＝恒等＝本番の乱数順・帯には触れない）。使い捨て。
    /// </summary>
    public sealed class PitchBudgetHudShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/pitch-budget";

        [Tooltip("撮りたい球数まで進める速度。")]
        [SerializeField] private float speed = 8f;

        [Tooltip("撮影用に落とす週間球数上限。序盤=消灯／中盤=点灯 の両方が出る値。")]
        [SerializeField] private int weeklyLimitForCapture = 200;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;   // 実進行（1球ポンプ）で回す
            var limit = weeklyLimitForCapture;
            MatchLiveController.CaptureContextOverride = ctx => ctx with { WeeklyPitchLimit = limit };
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[budget] MatchLive が見つからない"); yield break; }
            typeof(MatchLiveController)
                .GetField("_speed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(ctrl, speed);
            Directory.CreateDirectory(Dir);

            // ① 序盤＝分母だけ（週残チップは消灯）。
            yield return WaitFor(ctrl, () => Thrown(ctrl) >= 12, 120f);
            Log(ctrl, "序盤");
            yield return Capture(ctrl, "01-denominator");

            // ② 中盤＝残りが目安を割って「週残」チップが点く。
            yield return WaitFor(ctrl, () => Thrown(ctrl) >= 70, 240f);
            Log(ctrl, "中盤");
            yield return Capture(ctrl, "02-weekly-left");

            Debug.Log("[budget] DONE");
        }

        /// <summary>守備側投手の今日の球数（確定＋進行中）。</summary>
        private static int Thrown(MatchLiveController ctrl)
        {
            var def = Pitcher(ctrl);
            return def == null ? 0 : def.Pitches + def.PendingPitches;
        }

        private static KokoSim.Engine.Match.Game.LivePitcherToday Pitcher(MatchLiveController ctrl)
        {
            var at = ctrl.CurrentAtBatForCapture;
            var snap = ctrl.SnapshotForCapture();
            if (at == null || snap == null) return null;
            return at.IsTop ? snap.Home.Pitcher : snap.Away.Pitcher;
        }

        /// <summary>絵の裏取り（カメラの狙い外しと未表示は絵では区別できない）。</summary>
        private static void Log(MatchLiveController ctrl, string label)
        {
            var def = Pitcher(ctrl);
            if (def == null) return;
            var today = def.Pitches + def.PendingPitches;
            Debug.Log($"[budget] {label}: 球数={today} 目安={def.StaminaTarget:0.0} "
                + $"持ち越し={def.WeeklyPitchesPrior} 上限={def.WeeklyPitchLimit} "
                + $"週残={def.WeeklyPitchLimit - def.WeeklyPitchesPrior - today}");
        }

        private IEnumerator WaitFor(MatchLiveController ctrl, System.Func<bool> cond, float limitSeconds)
        {
            ctrl.FreezeLiveForCapture(false);
            var t = 0f;
            while (t < limitSeconds)
            {
                if (cond()) yield break;
                t += Time.deltaTime;
                yield return null;
            }
            Debug.LogWarning($"[budget] 条件が {limitSeconds}s 以内に立たなかった");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.FreezeLiveForCapture(true);   // 進行だけ止める（暗幕は出さない）
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            ctrl.DuelRigForCapture?.RenderNow();   // RT差し替え中に真っ黒で焼ける回への対策
            yield return null;

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
                Debug.Log("[budget] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
            ctrl.FreezeLiveForCapture(false);
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }

        private static MatchLiveController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
            }
            return null;
        }
    }
}
#endif

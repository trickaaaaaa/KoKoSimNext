#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #397 の受け入れスクショ。
    ///
    /// 対決ビューの球数・スタミナバーが打者交代までまとめて据え置かれていた問題の修正確認。
    /// <see cref="MatchLiveController.CaptureMode"/> は使わない（自動進行ごと止まると1球単位の
    /// 采配窓が再現できない＝<see cref="MatchLivePitchPumpShooter"/> と同じ理由）。
    /// 同一打席の2球目・4球目・6球目を撮り、球数（マッチアップHUD・スタメン列とも）が
    /// 1球ごとに動くことを絵で確認する。6球届かない打席もあるので、打者が変わったら
    /// 次の打席で撮り直す。使い捨て。
    /// </summary>
    public sealed class Issue397PitchCountShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue-397";

        [Tooltip("撮りたい場面まで進める速度。等倍だと6球粘る打席に届くまで長すぎる。")]
        [SerializeField] private float speed = 6f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;   // 実進行（1球ポンプ）で回す
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[397] MatchLive が見つからない"); yield break; }
            typeof(MatchLiveController)
                .GetField("_speed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(ctrl, speed);
            Directory.CreateDirectory(Dir);

            var attempts = 0;
            while (attempts++ < 30)
            {
                ctrl.FreezeLiveForCapture(false);
                yield return WaitFor(ctrl, () => ctrl.AtWindowForCapture
                    && ctrl.CurrentAtBatForCapture is { } a0 && a0.PitchNumberInPa >= 2, 90f);
                if (!_condHeld) { Debug.LogWarning("[397] 2球目の窓に届かなかった（打ち切り）"); yield break; }
                var anchor = KeyOf(ctrl.CurrentAtBatForCapture);
                LogPitchState(ctrl, "2球目");
                yield return Capture(ctrl, "01-pitch2");

                ctrl.FreezeLiveForCapture(false);
                yield return WaitFor(ctrl, () =>
                    !SameAtBat(ctrl, anchor) ||
                    (ctrl.AtWindowForCapture && ctrl.CurrentAtBatForCapture is { } a1 && a1.PitchNumberInPa >= 4), 60f);
                if (!SameAtBat(ctrl, anchor)) { Debug.Log("[397] 打者交代（4球届かず）。次の打席で再試行"); continue; }
                LogPitchState(ctrl, "4球目");
                yield return Capture(ctrl, "02-pitch4");

                ctrl.FreezeLiveForCapture(false);
                yield return WaitFor(ctrl, () =>
                    !SameAtBat(ctrl, anchor) ||
                    (ctrl.AtWindowForCapture && ctrl.CurrentAtBatForCapture is { } a2 && a2.PitchNumberInPa >= 6), 60f);
                if (!SameAtBat(ctrl, anchor)) { Debug.Log("[397] 打者交代（6球届かず）。次の打席で再試行"); continue; }
                LogPitchState(ctrl, "6球目");
                yield return Capture(ctrl, "03-pitch6");

                Debug.Log("[397] DONE 同一打席の2/4/6球目を撮影できた");
                yield break;
            }
            Debug.LogWarning("[397] 30回試行しても同一打席で6球に届かなかった");
        }

        private static (int Inning, bool IsTop, int Order) KeyOf(LiveAtBat a) => (a.Inning, a.IsTop, a.BatterOrder);

        private static bool SameAtBat(MatchLiveController ctrl, (int Inning, bool IsTop, int Order) anchor)
            => ctrl.CurrentAtBatForCapture is { } a && KeyOf(a) == anchor;

        /// <summary>スナップショットの守備側投手について、確定球数＋進行中ぶりの合計をログへ残す（絵の裏取り）。</summary>
        private static void LogPitchState(MatchLiveController ctrl, string label)
        {
            var at = ctrl.CurrentAtBatForCapture;
            var snap = ctrl.SnapshotForCapture();
            if (at == null || snap == null) return;
            var def = at.IsTop ? snap.Home.Pitcher : snap.Away.Pitcher;
            Debug.Log($"[397] {label}: PitchNumberInPa={at.PitchNumberInPa} "
                + $"Pitches={def.Pitches} PendingPitches={def.PendingPitches} "
                + $"StaminaRemaining={def.StaminaRemaining:0.000}");
        }

        private bool _condHeld;

        private IEnumerator WaitFor(MatchLiveController ctrl, System.Func<bool> cond, float limitSeconds)
        {
            _condHeld = false;
            var t = 0f;
            while (t < limitSeconds)
            {
                if (cond()) { _condHeld = true; yield break; }
                t += Time.deltaTime;
                yield return null;
            }
            Debug.LogWarning($"[397] 条件が {limitSeconds}s 以内に立たなかった");
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
                Debug.Log("[397] wrote " + file);
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

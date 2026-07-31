#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】1球進行（設計書15 §8）の受け入れスクショ。
    ///
    /// 既存シューターと違って <see cref="MatchLiveController.CaptureMode"/> を使わない——
    /// CaptureMode は自動進行ごと止めるので、この改修の肝である<b>采配窓</b>が再現できない。
    /// 実進行のまま任意のコマで <see cref="MatchLiveController.FreezeLiveForCapture"/> して撮る。
    /// 使い捨て。
    /// </summary>
    public sealed class MatchLivePitchPumpShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/matchlive-pitchpump";

        [Tooltip("撮りたい場面まで進める速度。等倍だと1試合が長すぎて目的の局面に届かない。")]
        [SerializeField] private float speed = 4f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = false;   // 実進行（1球ポンプ）で回す
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[PitchPump] MatchLive が見つからない"); yield break; }
            typeof(MatchLiveController)
                .GetField("_speed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(ctrl, speed);
            Directory.CreateDirectory(Dir);

            // ① 打席の頭の采配窓＝まだ1球も投げていないのに打者が出ている（この改修の肝）。
            yield return WaitFor(ctrl, () => ctrl.AtPaHeadWindowForCapture, 60f);
            yield return Capture(ctrl, "01-window-pa-head", "打席の頭の窓");

            // ② 自校が攻撃中の打席の頭＝攻撃カード10枚が押せる状態。
            yield return WaitFor(ctrl,
                () => ctrl.AtPaHeadWindowForCapture && ctrl.CurrentAtBatForCapture is { IsTop: false }, 90f);
            yield return Capture(ctrl, "02-window-offense-head", "自校攻撃の打席の頭");

            // ③ 走者ありの窓＝バント/盗塁/エンドランが「走者を見てから」選べる（旧: 出る前に押せた）。
            yield return WaitFor(ctrl,
                () => ctrl.AtWindowForCapture && ctrl.CurrentAtBatForCapture is { IsTop: false, BaseFirst: true }, 150f);
            yield return Capture(ctrl, "03-window-runner-on", "走者ありの窓");

            // ④ 打席の途中の窓＝カウントが進んだ状態でサインを出せる（1球単位である証拠）。
            yield return WaitFor(ctrl,
                () => ctrl.AtWindowForCapture
                      && ctrl.CurrentAtBatForCapture is { } a && a.PitchNumberInPa >= 3, 120f);
            yield return Capture(ctrl, "04-window-mid-count", "打席途中の窓");

            Debug.Log("[PitchPump] DONE");
        }

        // 直前の WaitFor が条件を満たして抜けたか（満たさず時間切れなら、撮った絵は「窓」ではない）。
        private bool _condHeld;

        /// <summary>条件が立つまで実進行を流す（凍結は解除しておく）。立たなければ諦めて先へ進む。</summary>
        private IEnumerator WaitFor(MatchLiveController ctrl, System.Func<bool> cond, float limitSeconds)
        {
            ctrl.FreezeLiveForCapture(false);
            _condHeld = false;
            var t = 0f;
            while (t < limitSeconds)
            {
                if (cond()) { _condHeld = true; yield break; }
                t += Time.deltaTime;
                yield return null;
            }
            Debug.LogWarning($"[PitchPump] 条件が {limitSeconds}s 以内に立たなかった（そのまま撮る＝窓の絵ではない）");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file, string what)
        {
            ctrl.FreezeLiveForCapture(true);   // 進行だけ止める（暗幕は出さない）
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            // 対決ビューはRT差し替え中に真っ黒で焼ける回があるので明示的に描き直す。
            ctrl.DuelRigForCapture?.RenderNow();
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
                var at = ctrl.CurrentAtBatForCapture;
                Debug.Log($"[PitchPump] wrote {file}（{what}／{(_condHeld ? "窓で撮れた" : "時間切れ＝窓ではない")}）"
                          + (at == null ? "" :
                    $" {at.Inning}回{(at.IsTop ? "表" : "裏")} {at.BatterOrder}番 {at.BatterName} " +
                    $"{at.Balls}-{at.Strikes} {at.PitchNumberInPa}球目 " +
                    $"塁{(at.BaseFirst ? "一" : "")}{(at.BaseSecond ? "二" : "")}{(at.BaseThird ? "三" : "")}"));
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

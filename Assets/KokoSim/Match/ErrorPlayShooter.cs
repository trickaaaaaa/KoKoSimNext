#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】エラー出塁の表示（issue #439）の受け入れ実測。使い捨て。
    /// 実シムの打席を進めてエラー判定イベント付きのプレーを見つけ、
    /// ・「エラー」テロップが確定の瞬間に出る（JudgementTextForCapture）
    /// ・一塁へ正着する送球（ToBase="first"）が描かれていない（捕球エラー＝弾く絵／悪送球＝逸れる絵）
    /// をログし、2D俯瞰（デバッグ）を点けて絵を撮る。
    /// </summary>
    public sealed class ErrorPlayShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/error-play-439";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Err439] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            var t = typeof(MatchLiveController);
            var fCurrent = t.GetField("_current", Priv);
            var fDuelOn = t.GetField("_duelOn", Priv);
            var mApplyView = t.GetMethod("ApplyDuelViewState", Priv);
            if (fCurrent == null || fDuelOn == null || mApplyView == null)
            { Debug.LogError("[Err439] リフレクション対象が見つからない"); yield break; }

            bool doneCatch = false, doneThrow = false;
            for (var pa = 0; pa < 400 && (!doneCatch || !doneThrow); pa++)
            {
                if (!ctrl.AdvanceForCapture()) break;
                var cur = fCurrent.GetValue(ctrl);
                var play = cur?.GetType().GetProperty("Play")?.GetValue(cur) as PlaybackPlay;
                if (play == null) continue;

                double errT = -1;
                foreach (var j in play.Judgements)
                    if (j.Kind == JudgementKind.Error) errT = j.T;
                if (errT < 0) continue;

                // 絵の監査: 一塁へ正着する送球が残っていないか＋どの種類のエラーの絵か。
                bool throwToFirst = false, errantThrow = false, hasCarry = false;
                var segs = new StringBuilder();
                foreach (var s in play.Ball)
                {
                    var eb = s as EndpointBallSegment;
                    var kind = eb != null ? eb.Kind.ToString() : s.GetType().Name;
                    segs.Append(kind).Append(s.ToBase != null ? "→" + s.ToBase : "").Append(" ");
                    if (s.ToBase == "first" && eb != null
                        && (eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw
                            || eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Carry))
                    {
                        if (eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Carry) hasCarry = true;
                        throwToFirst = true;
                    }
                    if (eb != null && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw
                        && s.ToBase == null) errantThrow = true;
                }
                var kindName = errantThrow ? "throw" : "catch";
                if (kindName == "catch" && doneCatch) continue;
                if (kindName == "throw" && doneThrow) continue;

                // 2D俯瞰（デバッグ）を点けて絵を出す（3D守備ビューはキャプチャ経路では回らないため）。
                fDuelOn.SetValue(ctrl, false);
                mApplyView.Invoke(ctrl, null);

                ctrl.SeekForCapture(errT + 0.15);
                yield return null;
                var telop = ctrl.JudgementTextForCapture;
                yield return Capture(ctrl, kindName + "-telop");
                ctrl.SeekForCapture(errT + 0.55);
                yield return null;
                yield return Capture(ctrl, kindName + "-after");

                fDuelOn.SetValue(ctrl, true);
                mApplyView.Invoke(ctrl, null);

                var ok = telop == "エラー" && !throwToFirst;
                Debug.Log($"[Err439] {kindName} {(ok ? "OK" : "NG")} 「{play.Result}」 判定T={errT:F2} " +
                          $"テロップ=「{telop}」 一塁正着送球={throwToFirst}(Carry={hasCarry}) 球=[{segs.ToString().Trim()}]");
                if (kindName == "catch") doneCatch = true; else doneThrow = true;
            }

            if (!doneCatch) Debug.LogWarning("[Err439] 捕球エラーのプレーが出なかった（再実行で引き直し）");
            if (!doneThrow) Debug.LogWarning("[Err439] 悪送球のプレーが出なかった（再実行で引き直し）");
            Debug.Log("[Err439] DONE → " + Dir);
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
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
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
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

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】表示同期（issue #438）の受け入れ実測。使い捨て。
    /// 実シムの打席を進めながら、
    ///  A) 打者セーフの安打: 結果テキストが旧時刻（ResAt）で出ず、到達（_resultShowAt）で出ること
    ///  B) 生還が決着より遅いプレー: 大型スコアの+1が各走者の本塁到達（_runScoreShowAt[k]）で起きること
    /// を RenderReplayFrame をリフレクションで直接駆動して検証し、前後の絵を撮る。
    /// </summary>
    public sealed class DisplaySyncShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/display-sync-438";
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Sync438] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            var t = typeof(MatchLiveController);
            var fCurrent = t.GetField("_current", Priv);
            var fT = t.GetField("_t", Priv);
            var fResultShowAt = t.GetField("_resultShowAt", Priv);
            var fRunShowAt = t.GetField("_runScoreShowAt", Priv);
            var fOwn = t.GetField("_scoreCallOwnScore", Priv);
            var fOpp = t.GetField("_scoreCallOppScore", Priv);
            var fCaption = t.GetField("_caption", Priv);
            var mPrepare = t.GetMethod("PrepareDisplayTimings", Priv);
            var mRender = t.GetMethod("RenderReplayFrame", Priv);
            var mHideCall = t.GetMethod("HideScoreCall", Priv);
            if (fCurrent == null || fT == null || fResultShowAt == null || fRunShowAt == null
                || fOwn == null || fOpp == null || fCaption == null
                || mPrepare == null || mRender == null || mHideCall == null)
            { Debug.LogError("[Sync438] リフレクション対象が見つからない（リネームされた？）"); yield break; }

            bool doneA = false, doneB = false;
            for (var pa = 0; pa < 200 && (!doneA || !doneB); pa++)
            {
                if (!ctrl.AdvanceForCapture()) break;
                var cur = fCurrent.GetValue(ctrl);
                var play = cur?.GetType().GetProperty("Play")?.GetValue(cur)
                    as KokoSim.Engine.Match.Timeline.Playback.PlaybackPlay;
                if (play == null) continue;
                var runs = (int)cur.GetType().GetProperty("RunsScored").GetValue(cur);

                mPrepare.Invoke(ctrl, new[] { cur });
                var resultShowAt = (double)fResultShowAt.GetValue(ctrl);
                var runShowAt = (IReadOnlyList<double>)fRunShowAt.GetValue(ctrl);

                // ── A) 打者セーフ: 結果テキスト（実況の結果行）は到達まで出ない ──
                // 現行の MatchLive 画面は実況ラベルを持たない（結果行の器は MatchDetail の2D再生）ため、
                // 「実試合→アダプタ→DLL」を通ってUnityに届いた play の実況を CaptionAt で検証する。
                if (!doneA && resultShowAt > play.ResAt + 0.3)
                {
                    var capOld = KokoSim.Engine.Match.Timeline.Playback.PlaybackEvaluator
                        .CaptionAt(play, play.ResAt + 0.05);
                    var capArrive = KokoSim.Engine.Match.Timeline.Playback.PlaybackEvaluator
                        .CaptionAt(play, resultShowAt + 0.05);

                    // 絵: 旧時刻（走者はまだ走っている・結果はまだ出ない）と到達の瞬間。
                    ctrl.SeekForCapture(0.0);
                    mHideCall.Invoke(ctrl, null);
                    mPrepare.Invoke(ctrl, new[] { cur });
                    fT.SetValue(ctrl, play.ResAt + 0.05); mRender.Invoke(ctrl, null);
                    yield return Capture(ctrl, "a-old-moment-no-text");
                    fT.SetValue(ctrl, resultShowAt + 0.05); mRender.Invoke(ctrl, null);
                    yield return Capture(ctrl, "a-arrival-text");

                    var ok = capOld != play.Result && capArrive == play.Result;
                    Debug.Log($"[Sync438] A {(ok ? "OK" : "NG")} 「{play.Result}」 ResAt={play.ResAt:F2} " +
                              $"到達={resultShowAt:F2} 旧時刻の実況=「{capOld}」 到達時の実況=「{capArrive}」");
                    doneA = true;
                }

                // ── B) 生還の遅いプレー: +1 が本塁到達で起きる ──
                if (!doneB && runs > 0 && runShowAt.Count > 0 && runShowAt[runShowAt.Count - 1] > play.ResAt + 0.8)
                {
                    ctrl.SeekForCapture(0.0);
                    mHideCall.Invoke(ctrl, null);
                    mPrepare.Invoke(ctrl, new[] { cur });

                    int Sum() => (int)fOwn.GetValue(ctrl) + (int)fOpp.GetValue(ctrl);
                    fT.SetValue(ctrl, play.ResAt + 0.05); mRender.Invoke(ctrl, null);
                    var okB = true;
                    var detail = $"ResAt={play.ResAt:F2}";
                    for (var k = 0; k < runShowAt.Count; k++)
                    {
                        fT.SetValue(ctrl, runShowAt[k] - 0.05); mRender.Invoke(ctrl, null);
                        var before = Sum();
                        if (k == runShowAt.Count - 1) yield return Capture(ctrl, "b-before-last-run");
                        fT.SetValue(ctrl, runShowAt[k] + 0.05); mRender.Invoke(ctrl, null);
                        var after = Sum();
                        if (k == runShowAt.Count - 1) yield return Capture(ctrl, "b-after-last-run");
                        okB &= after == before + 1;
                        detail += $" 到達{k + 1}={runShowAt[k]:F2}({before}→{after})";
                    }
                    Debug.Log($"[Sync438] B {(okB ? "OK" : "NG")} 「{play.Result}」 生還{runs} {detail}");
                    doneB = true;
                }
            }

            if (!doneA) Debug.LogWarning("[Sync438] Aケース（到達待ちの安打）が200打席で出なかった");
            if (!doneB) Debug.LogWarning("[Sync438] Bケース（遅い生還）が200打席で出なかった");
            Debug.Log("[Sync438] DONE → " + Dir);
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
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

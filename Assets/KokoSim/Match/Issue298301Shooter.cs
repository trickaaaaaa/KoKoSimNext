#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】Issue #298 / #301 の受け入れスクショ。
    /// 実試合（実エンジン出力）の俯瞰ビューを、狙った局面×複数時刻で撮り切る。
    ///
    /// 撮る局面（見つかった順に1組ずつ）:
    ///  A. 走者なしの打球 …… 投手がマウンドから動かないこと（#298-1）
    ///  B. 外野へ抜けた安打（着地後の転がり区間あり）…… 着地点→回収点へボールが転がり、
    ///     回収時刻に外野手とボールが重なること（#301①②）
    ///  C. 本塁への送球がある局面 …… 送球が距離ぶんの時間をかけて進むこと（#298-2/#301③）と、
    ///     走者ありでは投手が従来どおり本塁バックアップへ動くこと（#298-1の対比）
    ///
    /// 使い捨て。確認後に削除してよい。
    /// </summary>
    public sealed class Issue298301Shooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/i298-301";

        // 1局面あたり再生尺のどの割合で撮るか（打球→処理→返球を跨いで見る）。
        private static readonly double[] Fractions = { 0.15, 0.35, 0.55, 0.75, 0.95 };

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;   // 自動連続進行を止め AdvanceForCapture で手動駆動
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[i298301] MatchLive/Controller が見つからない"); yield break; }
            yield return null; // OnEnable で試合生成
            Directory.CreateDirectory(Dir);

            var doneA = false; var doneB = false; var doneC = false;
            var guard = 0;
            while (guard++ < 400 && (!doneA || !doneB || !doneC))
            {
                if (!ctrl.AdvanceForCapture()) break;
                var pa = ctrl.CurrentPa;
                if (pa == null || pa.Play == null) continue;

                var noRunners = !pa.BaseFirstBefore && !pa.BaseSecondBefore && !pa.BaseThirdBefore;
                var hasRoll = HasRollAfterFlight(pa.Play);
                var throwsHome = HasThrowToHome(pa.Play);

                if (!doneC && throwsHome && !noRunners)
                {
                    Debug.Log($"[i298301] C: {pa.Inning}回{(pa.IsTop ? "表" : "裏")} {pa.BatterName} {pa.Result} dur={pa.Play.Dur:F2}");
                    yield return CaptureSeries(ctrl, "C-backhome");
                    doneC = true;
                    continue;
                }
                if (!doneB && hasRoll)
                {
                    Debug.Log($"[i298301] B: {pa.Inning}回{(pa.IsTop ? "表" : "裏")} {pa.BatterName} {pa.Result} dur={pa.Play.Dur:F2}");
                    yield return CaptureSeries(ctrl, "B-outfield-roll");
                    doneB = true;
                    continue;
                }
                if (!doneA && noRunners)
                {
                    Debug.Log($"[i298301] A: {pa.Inning}回{(pa.IsTop ? "表" : "裏")} {pa.BatterName} {pa.Result} dur={pa.Play.Dur:F2}");
                    yield return CaptureSeries(ctrl, "A-norunner");
                    doneA = true;
                }
            }

            Debug.Log($"[i298301] DONE A={doneA} B={doneB} C={doneC} guard={guard}");
        }

        /// <summary>
        /// 着地（放物線）の後に転がり区間があるか＝外野へ抜けた飛球（#301②）。
        /// エンジン由来のプレーは種別付きの <see cref="EndpointBallSegment"/> になる
        /// （ArcSegment/RollSegment はモックサンプル専用の型なので型判定では拾えない）。
        /// </summary>
        private static bool HasRollAfterFlight(PlaybackPlay play)
        {
            var sawFlight = false;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null) continue;
                if (e.Kind == BallSegmentKind.Flight) { sawFlight = true; continue; }
                if (sawFlight && e.Kind == BallSegmentKind.Roll) return true;
            }
            return false;
        }

        /// <summary>本塁（原点付近）で終わる送球があるか。</summary>
        private static bool HasThrowToHome(PlaybackPlay play)
        {
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null || e.Kind != BallSegmentKind.Throw) continue;
                var at = e.At(e.T1 - e.T0);
                if (Mathf.Abs((float)at.X) < 1.5f && Mathf.Abs((float)at.Y) < 1.5f) return true;
            }
            return false;
        }

        /// <summary>現在ロード中の打席を、再生尺の複数時点で撮る。</summary>
        private IEnumerator CaptureSeries(MatchLiveController ctrl, string tag)
        {
            var dur = ctrl.CurrentPlayDuration;
            if (dur <= 0) yield break;
            for (var i = 0; i < Fractions.Length; i++)
            {
                var t = dur * Fractions[i];
                ctrl.SeekForCapture(t);
                yield return Shoot(ctrl, $"{tag}-{i}-t{t:F2}");
            }
        }

        private IEnumerator Shoot(MatchLiveController ctrl, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
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
                Debug.Log("[i298301] wrote " + file);
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

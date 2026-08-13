#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】失策3種（内野お手玉・悪送球・フライ落球）の絵の確認（issue #629）。使い捨て。
    ///
    /// <para><b>種別の見分け方</b>: engine の <c>FieldingErrorKind</c> は再生データに載らないので、
    /// ボール区間の形で分類する。逸れた送球は<b>受け手が居ない</b>（<c>ToRole == null</c>）＝悪送球、
    /// 放物線（Flight）を持つ失策＝フライ落球、どちらでもない失策＝内野のお手玉。</para>
    ///
    /// <para>撮るのは①失策の瞬間 ②拾い直したところ ③返球中。②③が出ていれば「拾った野手が棒立ち」
    /// （issue #629 ③）が直っていることになる。</para>
    ///
    /// 出力: screenshots/error-play-629/*.png ／ out/error-play-629/plays.tsv
    /// </summary>
    public sealed class ErrorKindPictureShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/error-play-629";
        private const string OutDir = "out/error-play-629";
        private const string SpawnFlag = "KokoSim.ErrorKindPictureShooter.Spawn";

        private const int MaxGames = 24;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~ErrorPlayAuto").AddComponent<ErrorKindPictureShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(OutDir);

            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[#629Shot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var log = new StringBuilder("試合\t打席\t結果\t種別\t撮った\n");
            var done = new HashSet<string>();

            for (var game = 1; game <= MaxGames && done.Count < 3; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(50000 + game * 5209));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;

                    // ⚠ 解決した直後の同じフレームで見る（次の Advance で再生ごと捨てられる）。
                    var play = _ctrl.CurrentPa?.Play;
                    var kind = ErrorKindOf(play);
                    var want = kind != null && !done.Contains(kind);

                    log.Append(game).Append('\t').Append(plate).Append('\t')
                       .Append(play?.Result ?? "-").Append('\t').Append(kind ?? "—").Append('\t')
                       .Append(want ? "★" : "—").Append('\n');

                    if (!want) continue;

                    done.Add(kind);
                    yield return ShootPlay(kind, play, game, plate);
                    if (done.Count >= 3) break;

                    var waited = 0;
                    while (_ctrl.FieldPlayActiveForCapture && waited++ < 900) yield return null;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), log.ToString());
            Debug.Log($"[#629Shot] 撮れた種別={string.Join(",", done)} → {ShotDir}");
            Time.captureDeltaTime = 0f;
        }

        private IEnumerator RestartWithSeed(ulong seed)
        {
            MatchLiveController.GameSeedOverrideForCapture = seed;
            _liveGo.SetActive(false);
            yield return null;
            _liveGo.SetActive(true);
            yield return null; yield return null;
        }

        // --- 再生データの読み取り（構築はしない＝required メンバーに触れないので C# 9 で通る） ---

        /// <summary>失策の種別（"catch"/"throw"/"fly"）。失策でなければ null。</summary>
        private static string ErrorKindOf(PlaybackPlay play)
        {
            if (play == null || play.Result == null || !play.Result.Contains("エラー")) return null;
            var fly = false;
            var errant = false;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null) continue;
                if (e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Flight) fly = true;
                // 逸れた送球は受け手が居ない（issue #439 の「完璧な送球が先着する絵にしない」）。
                if (e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw && e.ToRole == null) errant = true;
            }
            if (errant) return "throw";
            return fly ? "fly" : "catch";
        }

        /// <summary>失策球が止まる時刻（＝拾い上げに向かう頃）。</summary>
        private static double DeflectEndOf(PlaybackPlay play)
        {
            var t = 0.0;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null) continue;
                if (e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Roll && e.T1 > t) t = e.T1;
                if (e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw && e.ToRole == null && e.T1 > t) t = e.T1;
            }
            return t;
        }

        /// <summary>返球（拾い直した後の送球＝受け手あり）の到達時刻。無ければ 0。</summary>
        private static double ReturnArriveOf(PlaybackPlay play)
        {
            var t = 0.0;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null) continue;
                if (e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw && e.ToRole != null && e.T1 > t) t = e.T1;
            }
            return t;
        }

        // --- 撮影 ---

        private IEnumerator ShootPlay(string tag, PlaybackPlay play, int game, int plate)
        {
            var waited = 0;
            while (!_ctrl.FieldPlayActiveForCapture && waited++ < 1800) yield return null;
            if (!_ctrl.FieldPlayActiveForCapture)
            {
                Debug.LogWarning($"[#629Shot] {tag}: 守備再生が始まらないまま打ち切り（{game}試合目 {plate}打席目）");
                yield break;
            }

            var deflect = DeflectEndOf(play);
            var back = ReturnArriveOf(play);

            yield return WaitClock(deflect);
            yield return Shoot(tag + "-1-error");           // 弾いた／逸れた直後
            yield return WaitClock(deflect + 1.0);
            yield return Shoot(tag + "-2-pickup");          // 拾い直したところ
            if (back > 0)
            {
                yield return WaitClock(back);
                yield return Shoot(tag + "-3-return");      // 返球の到達
            }
            Debug.Log($"[#629Shot] {tag} 撮影（{game}試合目 {plate}打席目 {play.Result} 返球到達={back:F2}s）");
        }

        private IEnumerator WaitClock(double t)
        {
            var guard = 0;
            while (_ctrl.FieldPlayActiveForCapture && _ctrl.PlayClockForCapture < t && guard++ < 900) yield return null;
        }

        /// <summary>画面まるごと1枚（撮影の間は進行を凍らせる＝節目がずれない。#595 と同じ作法）。</summary>
        private IEnumerator Shoot(string file)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            _ctrl.DuelRigForCapture?.RenderNow();
            yield return null;

            var ps = _ctrl.GetComponent<UIDocument>().panelSettings;
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
                File.WriteAllBytes(Path.Combine(ShotDir, file + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
                _ctrl.FreezeLiveForCapture(false);
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

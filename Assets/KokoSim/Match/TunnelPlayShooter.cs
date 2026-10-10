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
    /// 【開発用・エディタ限定】トンネル（股・脇を抜けて球が外野へ行く失策, issue #632）の絵の確認。使い捨て。
    ///
    /// <para><b>見分け方</b>: engine の <c>FieldingErrorKind</c> は再生データに載らないので<b>球の行き先</b>で見る。
    /// 失策なのに球が内野の外（46m超）まで転がって拾われる＝抜けた球。お手玉（3m）も悪送球（一塁の脇）も
    /// 内野の中で止まるので、この1条件で分離できる。</para>
    ///
    /// <para>撮るのは①抜かれた瞬間（「エラー」テロップがここで出る＝回収時刻ではない）②外野手が回収
    /// ③返球の到達。これが揃っていれば「どんな失策でも球が内野から出ない」（issue #632 の症状）が
    /// 直っていることになる。</para>
    ///
    /// 出力: screenshots/tunnel-632/*.png ／ out/tunnel-632/plays.tsv
    /// </summary>
    public sealed class TunnelPlayShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/tunnel-632";
        private const string OutDir = "out/tunnel-632";
        private const string SpawnFlag = "KokoSim.TunnelPlayShooter.Spawn";

        private const int MaxGames = 40;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~TunnelPlayAuto").AddComponent<TunnelPlayShooter>();
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
            if (_ctrl == null) { Debug.LogError("[#632Shot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var log = new StringBuilder("試合\t打席\t結果\t種別\t撮った\n");
            var done = new HashSet<string>();

            for (var game = 1; game <= MaxGames && done.Count < 1; game++)
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
                    break;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), log.ToString());
            Debug.Log($"[#632Shot] 撮れた={string.Join(",", done)} → {ShotDir}");
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

        /// <summary>内野の外縁[m]（これを越えた所で球が拾われていれば外野へ抜けている）。</summary>
        private const double InfieldDepthM = 46.0;

        /// <summary>
        /// トンネル（issue #632）なら "tunnel"、それ以外の失策なら null。
        /// engine の <c>FieldingErrorKind</c> は再生データに載らないので<b>球の行き先</b>で見分ける:
        /// 失策なのに球が内野の外（46m超）まで転がって拾われる＝抜けた球。
        /// お手玉（3m）・悪送球（一塁の脇）はどちらも内野の中で止まる。
        /// </summary>
        private static string ErrorKindOf(PlaybackPlay play)
        {
            if (play == null || play.Result == null || !play.Result.Contains("エラー")) return null;
            // ⚠ 放物線（Flight）を持つ失策は外野のフライ落球（issue #629）。落球の絵も着地点＝46m超で
            // 転がるので、距離だけで見るとトンネルと区別できない（初回キャプチャで「レフトのエラー」＝
            // フライ落球を掴んだ実害）。ゴロだけに絞る。
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e != null && e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Flight) return null;
            }
            return RollEndDistance(play) > InfieldDepthM ? "tunnel" : null;
        }

        /// <summary>転がり区間の終端（＝回収点）の本塁からの距離[m]。転がりが無ければ 0。</summary>
        private static double RollEndDistance(PlaybackPlay play)
        {
            var best = 0.0;
            foreach (var s in play.Ball)
            {
                var e = s as EndpointBallSegment;
                if (e == null || e.Kind != KokoSim.Engine.Match.Timeline.BallSegmentKind.Roll) continue;
                var end = e.At(e.T1 - e.T0);   // 端点は非公開なので区間終端を評価して採る
                var d = Mathf.Sqrt((float)(end.X * end.X + end.Y * end.Y));
                if (d > best) best = d;
            }
            return best;
        }

        /// <summary>「エラー」が確定した時刻（＝内野手が抜かれた瞬間, issue #632）。無ければ 0。</summary>
        private static double ErrorMomentOf(PlaybackPlay play)
        {
            foreach (var j in play.Judgements)
            {
                if (j.Kind == KokoSim.Engine.Match.Timeline.JudgementKind.Error) return j.T;
            }
            return 0.0;
        }

        /// <summary>失策球が拾われる時刻（＝転がり区間の終端）。</summary>
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
                Debug.LogWarning($"[#632Shot] {tag}: 守備再生が始まらないまま打ち切り（{game}試合目 {plate}打席目）");
                yield break;
            }

            var beaten = ErrorMomentOf(play);               // 内野手が抜かれた瞬間（テロップが出る時刻）
            var pickup = DeflectEndOf(play);                // 外野手が球に追いついた時刻
            var back = ReturnArriveOf(play);

            if (beaten > 0)
            {
                // ⚠ 「エラー」の表示は物理時刻からワンテンポ遅れる（PlaybackEvaluator.ErrorDisplayDelaySeconds）。
                // 判定時刻ちょうどで撮るとテロップの出る前の絵になる（初回キャプチャの実害）。
                yield return WaitClock(beaten + PlaybackEvaluator.ErrorDisplayDelaySeconds + 0.15);
                yield return Shoot(tag + "-1-beaten");      // 抜かれた瞬間（「エラー」テロップ）
            }
            yield return WaitClock(pickup);
            yield return Shoot(tag + "-2-retrieve");        // 外野手が回収
            if (back > 0)
            {
                yield return WaitClock(back);
                yield return Shoot(tag + "-3-return");      // 返球の到達
            }
            Debug.Log($"[#632Shot] {tag} 撮影（{game}試合目 {plate}打席目 {play.Result} 返球到達={back:F2}s）");
        }

        private IEnumerator WaitClock(double t)
        {
            var guard = 0;
            while (_ctrl.FieldPlayActiveForCapture && _ctrl.PlayClockForCapture < t && guard++ < 900) yield return null;
        }

        /// <summary>画面まるごと1枚（撮影の間は進行を凍らせる＝節目がずれない。#595 と同じ作法）。</summary>
        private IEnumerator Shoot(string file)
        {
            yield return ShootOne(file, false);
            yield return ShootOne(file + "-2d", true);
        }

        /// <summary>2D俯瞰（デバッグ）の on/off。private フィールド＋private メソッドなのでリフレクション。</summary>
        private void SetOverhead(bool on)
        {
            var t = typeof(MatchLiveController);
            var f = t.GetField("_duelOn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var m = t.GetMethod("ApplyDuelViewState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f == null || m == null) return;
            f.SetValue(_ctrl, !on);
            m.Invoke(_ctrl, null);
        }

        /// <summary>3Dのカメラは球を追って動くので、球がどこへ行ったかは画角固定の2Dでしか読めない（#630 と同じ）。</summary>
        private IEnumerator ShootOne(string file, bool overhead)
        {
            _ctrl.FreezeLiveForCapture(true);
            if (overhead) SetOverhead(true);
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
                if (overhead) SetOverhead(false);
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

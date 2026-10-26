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
    /// 【開発用・エディタ限定】守備の芝居（横っ飛び・スライディング・背走ジャンピング・お手玉・
    /// 飛び込んだが届かない）が絵で読めるかの実機確認（issue wire-unused-defender-motions）。使い捨て。
    ///
    /// <para>撮るのは「捕球の瞬間」の前後3コマ。<b>接地は絵では判定できない</b>（めり込みも浮きも
    /// 「そういう姿勢」に見える）ので、体の最下点の対地高を TSV に落とす（design-18 §3.3 と同じ理由）。</para>
    ///
    /// 出力: screenshots/defender-motions/*.png ／ out/defender-motions/motions.tsv
    /// </summary>
    public sealed class DefenderMotionShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/defender-motions";
        private const string OutDir = "out/defender-motions";
        private const string SpawnFlag = "KokoSim.DefenderMotionShooter.Spawn";

        private const int MaxGames = 40;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        /// <summary>true＝スライディングキャッチ（捕球余裕 +0.10〜+0.20s の外野フライ）だけを撮る（issue #674）。</summary>
        public static bool SlideOnly;
        /// <summary>SlideOnly のとき撮る本数（接触の 0.4s 前〜1.4s 後を等間隔）。</summary>
        public static int SlideFrames = 8;
        /// <summary>SlideOnly で撮る場面の数。</summary>
        public static int SlideScenes = 3;
        /// <summary>SlideOnly で撮る捕球余裕の帯[s]（既定=スライディング +0.10〜+0.20。横っ飛びは 0〜+0.10）。</summary>
        public static double SlideMargin0 = 0.10, SlideMargin1 = 0.20;
        /// <summary>出力ファイル名の頭（帯ごとに撮り分ける）。</summary>
        public static string SlideTag = "slide";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~DefenderMotionAuto").AddComponent<DefenderMotionShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;
        private StringBuilder _log;

        /// <summary>撮りたい場面（engine の値だけで判定する＝絵の側で結果を推測しない）。</summary>
        private enum Want { Dive, DiveMiss, Fumble }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);   // ⚠ 撮影中に消さない（書き込みが DirectoryNotFound で落ちる）
            Directory.CreateDirectory(OutDir);

            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[守備の芝居] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            _log = new StringBuilder("場面\t捕球余裕[s]\t再生時計[s]\t実況\t内訳と接地\n");
            var todo = SlideOnly ? new HashSet<Want> { Want.Dive } : new HashSet<Want> { Want.Dive, Want.DiveMiss, Want.Fumble };
            var slideLeft = SlideScenes;

            for (var game = 1; game <= MaxGames && todo.Count > 0; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(70000 + game * 7919));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver && todo.Count > 0; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;
                    var play = _ctrl.CurrentPa?.Play;
                    var want = Classify(play);
                    if (play == null || want == null || !todo.Contains(want.Value))
                    {
                        _ctrl.FreezeCurrentAtResult();
                        yield return null;
                        continue;
                    }
                    if (SlideOnly)
                    {
                        // スライディング帯（+0.10〜+0.20s）かつ外野のフライだけ。横っ飛び帯・内野ゴロは撮らない
                        if (play.CatchMargin is not { } sm || sm < SlideMargin0 || sm >= SlideMargin1) { _ctrl.FreezeCurrentAtResult(); yield return null; continue; }
                        if (--slideLeft <= 0) todo.Remove(want.Value);
                        var tcs = FirstFlightEnd(play);
                        var tag = SlideTag + "-" + (SlideScenes - slideLeft);
                        Debug.Log($"[守備の芝居] {tag} 余裕={play.CatchMargin:F2}s 接触={tcs:F2}s");
                        for (var k = 0; k < SlideFrames; k++)
                        {
                            var tk = tcs - 0.40 + k * (1.8 / (SlideFrames - 1));
                            yield return PlayUntil(tk);
                            yield return Shoot($"{tag}-f{k}", $"{tag} t={tk - tcs:+0.00;-0.00}", play);
                        }
                        _ctrl.FreezeCurrentAtResult();
                        yield return null;
                        continue;
                    }
                    todo.Remove(want.Value);

                    // 「捕球の瞬間」＝engine の球が野手へ着く時刻（最初の滞空区間の終端）
                    var tc = FirstFlightEnd(play);
                    var name = want.Value == Want.Dive ? "1-dive"
                        : want.Value == Want.DiveMiss ? "2-dive-miss" : "3-fumble";
                    Debug.Log($"[守備の芝居] {name} 余裕={play.CatchMargin:F2}s 接触={tc:F2}s");

                    yield return PlayUntil(tc - 0.30);
                    yield return Shoot(name + "-a-before", name + " 手前", play);
                    yield return PlayUntil(tc + 0.02);
                    yield return Shoot(name + "-b-moment", name + " 瞬間", play);
                    yield return PlayUntil(tc + 0.50);
                    yield return Shoot(name + "-c-after", name + " 直後", play);
                    _ctrl.FreezeCurrentAtResult();
                    yield return null;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "motions.tsv"), _log.ToString());
            Debug.Log($"[守備の芝居] 残り {todo.Count} 種は出ないまま終了 → {ShotDir}");
            Time.captureDeltaTime = 0f;
        }

        /// <summary>engine の観測値だけで場面を判定する（絵や結果テキストから推測しない）。</summary>
        private static Want? Classify(PlaybackPlay play)
        {
            if (play == null) return null;
            if (play.Fumbled) return Want.Fumble;
            if (play.CatchMargin is not { } m) return null;
            const double dive = 0.20;   // DuelStageRig.DiveMarginSeconds と同値
            if (m >= 0 && m < dive) return Want.Dive;
            if (m < 0 && m > -dive) return Want.DiveMiss;
            return null;
        }

        private static double FirstFlightEnd(PlaybackPlay play)
        {
            foreach (var b in play.Ball)
                if (b is EndpointBallSegment e && e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Flight)
                    return e.T1;
            return play.Ball.Count > 0 ? play.Ball[0].T1 : 1.0;
        }

        private IEnumerator PlayUntil(double t)
        {
            var waited = 0;
            while (waited++ < 3000 && !(_ctrl.StageNameForCapture == "Play" && _ctrl.PlayClockForCapture > 0))
                yield return null;
            var guard = 0;
            while (guard++ < 3000 && _ctrl.PlayClockForCapture < t && _ctrl.StageNameForCapture == "Play")
                yield return null;
        }

        private IEnumerator Shoot(string file, string frame, PlaybackPlay play)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            _ctrl.DuelRigForCapture?.RenderNow();
            yield return null;

            var t = _ctrl.PlayClockForCapture;
            _log.Append(frame).Append('\t')
                .Append(play.CatchMargin.HasValue ? play.CatchMargin.Value.ToString("F2") : "-").Append('\t')
                .Append(t.ToString("F2")).Append('\t')
                .Append(PlaybackEvaluator.CaptionAt(play, t)).Append('\t')
                .Append(_ctrl.DuelRigForCapture?.FieldDiveDiagForCapture() ?? "-").Append('\n');

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

        private IEnumerator RestartWithSeed(ulong seed)
        {
            MatchLiveController.GameSeedOverrideForCapture = seed;
            _liveGo.SetActive(false);
            yield return null;
            _liveGo.SetActive(true);
            yield return null; yield return null;
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

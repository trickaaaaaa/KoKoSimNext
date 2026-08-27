#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】外野安打で本塁への安全な生還と、別走者の三塁憤死が同じ打球で
    /// 同時に起きる場面の絵の確認（issue outfield-hit-home-safe-and-third-out-throw-missing）。
    ///
    /// <para>修正前は本塁への演出用送球が無条件で描かれ、実際にアウトを取った三塁への送球が
    /// 握りつぶされて消えていた（＝カメラが本塁に居座ったまま三塁の憤死が画面に映らない）。
    /// この使い捨てハーネスは <see cref="OutfieldReturnThirdShooter"/>（issue #627）と同じ探索法で、
    /// 「三塁憤死」に加えて「同じ打球で別走者が本塁へ安全生還」も条件に含める。</para>
    ///
    /// 出力: screenshots/home-safe-third-out/*.png ／ out/home-safe-third-out/plays.tsv
    /// </summary>
    public sealed class HomeSafeAndThirdOutShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/home-safe-third-out";
        private const string OutDir = "out/home-safe-third-out";
        private const string SpawnFlag = "KokoSim.HomeSafeAndThirdOutShooter.Spawn";

        private const int MaxGames = 60;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~HomeSafeAndThirdOutAuto").AddComponent<HomeSafeAndThirdOutShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;
        private bool _shot;
        private readonly StringBuilder _log = new StringBuilder("試合\t打席\t結果\t返球先\t三塁憤死\t本塁生還\t撮った\n");

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
            if (_ctrl == null) { Debug.LogError("[HomeThirdShot] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            var got = false;

            for (var game = 1; game <= MaxGames && !got; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(70000 + game * 7919));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;

                    var play = _ctrl.CurrentPa?.Play;
                    var target = ThrowTargetOf(play);
                    var runnerToThird = RunnerReachingThird(play, out var runnerOut);
                    var scoredSafely = RunnerReachingHomeSafely(play);
                    var want = target == "三塁" && runnerToThird && runnerOut && FromOutfield(play) && scoredSafely;

                    _log.Append(game).Append('\t').Append(plate).Append('\t')
                        .Append(play?.Result ?? "-").Append('\t').Append(target).Append('\t')
                        .Append(runnerOut ? "憤死" : "—").Append('\t')
                        .Append(scoredSafely ? "生還" : "—").Append('\t').Append(want ? "★" : "—").Append('\n');

                    if (!want) continue;

                    _shot = false;
                    yield return ShootPlay(ThrowArriveOf(play), game, plate);
                    if (!_shot) continue;
                    got = true;
                    break;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), _log.ToString());
            Debug.Log($"[HomeThirdShot] {(got ? "撮れた" : "★出ず")} → {ShotDir}");
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

        private static bool NearBase(double x, double y, double bx, double by)
            => System.Math.Abs(x - bx) < 2.0 && System.Math.Abs(y - by) < 2.0;

        private static EndpointBallSegment LastThrow(PlaybackPlay play)
        {
            if (play == null) return null;
            EndpointBallSegment last = null;
            foreach (var s in play.Ball)
                if (s is EndpointBallSegment e && e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw
                    && (last == null || e.T1 > last.T1)) last = e;
            return last;
        }

        private static string ThrowTargetOf(PlaybackPlay play)
        {
            var last = LastThrow(play);
            if (last == null) return "—";
            switch (last.ToBase)
            {
                case "third": return "三塁";
                case "second": return "二塁";
                case "first": return "一塁";
                case "home": return "本塁";
            }
            var p = last.At(last.T1 - last.T0);
            if (NearBase(p.X, p.Y, -19.40, 19.40)) return "三塁";
            if (NearBase(p.X, p.Y, 0.0, 38.79)) return "二塁";
            if (NearBase(p.X, p.Y, 0.0, 0.0)) return "本塁";
            if (NearBase(p.X, p.Y, 19.40, 19.40)) return "一塁";
            return "他";
        }

        private static bool FromOutfield(PlaybackPlay play)
        {
            if (play == null) return false;
            EndpointBallSegment first = null;
            foreach (var s in play.Ball)
                if (s is EndpointBallSegment e && e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw
                    && (first == null || e.T0 < first.T0)) first = e;
            var r = first?.FromRole;
            return r == KokoSim.Engine.Match.Field.FieldPosition.LeftField
                || r == KokoSim.Engine.Match.Field.FieldPosition.CenterField
                || r == KokoSim.Engine.Match.Field.FieldPosition.RightField;
        }

        private static double ThrowArriveOf(PlaybackPlay play)
        {
            var last = LastThrow(play);
            return last?.T1 ?? 0.0;
        }

        /// <summary>打者走者**以外**が三塁へ達しているか。</summary>
        private static bool RunnerReachingThird(PlaybackPlay play, out bool runnerOut)
        {
            runnerOut = false;
            if (play == null) return false;
            foreach (var r in play.Runners)
            {
                if (r.Label == "打" || r.Segs.Count == 0) continue;
                var lastLeg = r.Segs[r.Segs.Count - 1];
                if (!NearBase(lastLeg.To.X, lastLeg.To.Y, -19.40, 19.40)) continue;
                runnerOut = lastLeg.OutAtEnd;
                return true;
            }
            return false;
        }

        /// <summary>三塁で憤死した走者とは別の走者が、本塁へ安全に生還しているか。</summary>
        private static bool RunnerReachingHomeSafely(PlaybackPlay play)
        {
            if (play == null) return false;
            foreach (var r in play.Runners)
            {
                if (r.Label == "打" || r.Segs.Count == 0) continue;
                var lastLeg = r.Segs[r.Segs.Count - 1];
                if (lastLeg.OutAtEnd) continue;
                if (!NearBase(lastLeg.To.X, lastLeg.To.Y, 0.0, 0.0)) continue;
                var moved = System.Math.Abs(lastLeg.To.X - lastLeg.From.X) > 0.5
                    || System.Math.Abs(lastLeg.To.Y - lastLeg.From.Y) > 0.5;
                if (moved) return true;
            }
            return false;
        }

        private IEnumerator ShootPlay(double arriveAt, int game, int plate)
        {
            yield return WaitFieldPlay();
            if (!_ctrl.FieldPlayActiveForCapture)
            {
                Debug.LogWarning($"[HomeThirdShot] 守備再生が始まらないまま打ち切り（{game}試合目 {plate}打席目）");
                yield break;
            }

            yield return WaitClock(System.Math.Max(0.0, arriveAt - 1.0));
            yield return Shoot("1-throw");
            yield return WaitClock(arriveAt);
            yield return Shoot("2-arrive");

            _shot = true;
            Debug.Log($"[HomeThirdShot] 撮影（{game}試合目 {plate}打席目 送球到達={arriveAt:F2}s）");
        }

        private IEnumerator WaitFieldPlay()
        {
            var waited = 0;
            while (!_ctrl.FieldPlayActiveForCapture && waited++ < 1800) yield return null;
        }

        private IEnumerator WaitClock(double t)
        {
            var guard = 0;
            while (_ctrl.FieldPlayActiveForCapture && _ctrl.PlayClockForCapture < t && guard++ < 900) yield return null;
        }

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

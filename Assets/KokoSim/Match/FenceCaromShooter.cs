#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】フェンス直撃の跳ね返りが絵で読めるかの実機確認
    /// （issue fence-carom-and-over-the-head-cues）。使い捨て。
    ///
    /// <para>撮るのは ①頭を越えた瞬間（着地） ②フェンス直撃 ③跳ね返って戻る球 ④回収。
    /// 「読めるか」は絵で見るが、<b>戻った距離</b>は絵では測れないので球の位置を TSV に落とす。</para>
    ///
    /// 出力: screenshots/fence-carom/*.png ／ out/fence-carom/carom.tsv
    /// </summary>
    public sealed class FenceCaromShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/fence-carom";
        private const string OutDir = "out/fence-carom";
        private const string SpawnFlag = "KokoSim.FenceCaromShooter.Spawn";

        private const int MaxGames = 24;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~FenceCaromAuto").AddComponent<FenceCaromShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;
        private StringBuilder _log;

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
            if (_ctrl == null) { Debug.LogError("[跳ね返り] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            _log = new StringBuilder("コマ\t再生時計[s]\t球の本塁からの距離[m]\t実況\n");
            var shot = false;

            for (var game = 1; game <= MaxGames && !shot; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(30000 + game * 8887));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;
                    var play = _ctrl.CurrentPa?.Play;
                    // 跳ね返りのあるプレー＝実況に「フェンス直撃！」が載っている（engine が単一ソース）。
                    var hitAt = play?.Caps.FirstOrDefault(c => c.Text == "フェンス直撃！").T ?? -1.0;
                    if (play == null || hitAt <= 0)
                    {
                        _ctrl.FreezeCurrentAtResult();
                        yield return null;
                        continue;
                    }

                    shot = true;
                    var rolls = play.Ball.Where(b => b is EndpointBallSegment e && e.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Roll).ToList();
                    var settleAt = rolls.Count > 0 ? rolls[rolls.Count - 1].T1 : hitAt + 1.0;
                    Debug.Log($"[跳ね返り] 直撃 {hitAt:F2}s → 回収 {settleAt:F2}s");

                    yield return PlayUntil(hitAt - 0.6);
                    yield return Shoot("1-over-the-head", "①頭を越えた", play);
                    yield return PlayUntil(hitAt + 0.05);
                    yield return Shoot("2-fence-hit", "②フェンス直撃", play);
                    yield return PlayUntil((hitAt + settleAt) * 0.5);
                    yield return Shoot("3-rebounding", "③戻ってくる球", play);
                    yield return PlayUntil(settleAt);
                    yield return Shoot("4-retrieved", "④回収", play);
                    break;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "carom.tsv"), _log.ToString());
            Debug.Log($"[跳ね返り] {(shot ? "撮れた" : "★フェンス直撃が出ないまま打ち切り")} → {ShotDir}");
            Time.captureDeltaTime = 0f;
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
            var ball = PlaybackEvaluator.BallAt(play, t);
            var range = ball.HasValue ? Mathf.Sqrt((float)(ball.Value.X * ball.Value.X + ball.Value.Y * ball.Value.Y)) : -1f;
            _log.Append(frame).Append('\t').Append(t.ToString("F2")).Append('\t')
                .Append(range.ToString("F1")).Append('\t')
                .Append(PlaybackEvaluator.CaptionAt(play, t)).Append('\n');

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

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
    /// 【開発用・エディタ限定】「ホームラン」表示が打者の本塁到達まで出続けることの実機確認
    /// （issue homerun-call-until-home-touch）。使い捨て。
    ///
    /// <para>撮るのは ①着地直後 ②三塁付近（従来はここが空白だった） ③本塁到達（得点コールと同時）。
    /// 監督決定「判定と得点コールは両方出す。画面上で重ならないことを実機で確認する」に応えるため、
    /// ③では<b>2つの器の実寸（worldBound）と重なり量</b>を TSV に落とす（目測で判定しない）。</para>
    ///
    /// 出力: screenshots/homerun-call/*.png ／ out/homerun-call/overlap.tsv
    /// </summary>
    public sealed class HomeRunCallShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/homerun-call";
        private const string OutDir = "out/homerun-call";
        private const string SpawnFlag = "KokoSim.HomeRunCallShooter.Spawn";

        private const int MaxGames = 24;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~HomeRunCallAuto").AddComponent<HomeRunCallShooter>();
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
            if (_ctrl == null) { Debug.LogError("[本塁打コール] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            _log = new StringBuilder("コマ\t再生時計[s]\t判定文字\t判定の器 y[px]\t得点コール y[px]\t重なり[px]\n");
            var shot = false;

            for (var game = 1; game <= MaxGames && !shot; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(60000 + game * 3671));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;
                    var pa = _ctrl.CurrentPa;
                    var play = pa?.Play;
                    var land = HomeRunLandingTime(play);
                    var touch = play == null ? null : PlaybackEvaluator.BatterHomeTouchTime(play);
                    if (land is not { } l || touch is not { } th)
                    {
                        _ctrl.FreezeCurrentAtResult();
                        yield return null;
                        continue;
                    }

                    shot = true;
                    Debug.Log($"[本塁打コール] 着地 {l:F2}s → 本塁到達 {th:F2}s（{th - l:F2}s）");
                    yield return PlayUntil(l + 0.3);
                    yield return Shoot("1-landed", "①着地直後");
                    yield return PlayUntil(l + (th - l) * 0.65);
                    yield return Shoot("2-rounding-third", "②三塁付近");
                    yield return PlayUntil(th + 0.15);
                    yield return Shoot("3-home-touch", "③本塁到達");
                    break;
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "overlap.tsv"), _log.ToString());
            Debug.Log($"[本塁打コール] {(shot ? "撮れた" : "★本塁打が出ないまま打ち切り")} → {ShotDir}");
            Time.captureDeltaTime = 0f;
        }

        private static double? HomeRunLandingTime(PlaybackPlay play)
        {
            if (play == null) return null;
            foreach (var j in play.Judgements)
            {
                if (j.Kind == KokoSim.Engine.Match.Timeline.JudgementKind.HomeRun) return j.T;
            }
            return null;
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

        private IEnumerator Shoot(string file, string frame)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            _ctrl.DuelRigForCapture?.RenderNow();
            yield return null;

            // 2つの器の実寸を採る（重なりは目測で判定しない）。非表示なら y = -1。
            var root = _ctrl.GetComponent<UIDocument>().rootVisualElement;
            var judge = root.Q<VisualElement>("judgement-call");
            var score = root.Q<VisualElement>("score-call");
            var jr = RectOf(judge);
            var sr = RectOf(score);
            var overlap = jr.HasValue && sr.HasValue
                ? Mathf.Max(0f, Mathf.Min(jr.Value.yMax, sr.Value.yMax) - Mathf.Max(jr.Value.yMin, sr.Value.yMin))
                : 0f;

            _log.Append(frame).Append('\t').Append(_ctrl.PlayClockForCapture.ToString("F2")).Append('\t')
                .Append(_ctrl.JudgementTextForCapture).Append('\t')
                .Append(jr.HasValue ? $"{jr.Value.yMin:F0}〜{jr.Value.yMax:F0}" : "非表示").Append('\t')
                .Append(sr.HasValue ? $"{sr.Value.yMin:F0}〜{sr.Value.yMax:F0}" : "非表示").Append('\t')
                .Append(overlap.ToString("F0")).Append('\n');

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

        /// <summary>表示中の要素の実寸（非表示なら null）。</summary>
        private static Rect? RectOf(VisualElement e)
        {
            if (e == null || e.resolvedStyle.display == DisplayStyle.None) return null;
            var r = e.worldBound;
            return float.IsNaN(r.yMin) ? (Rect?)null : r;
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

#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】守備ビューが全景（縦155m）へ跳ばないことの実機確認
    /// （issue field-view-cut-back-sooner）。使い捨て。
    ///
    /// <para>撮るのは<b>これまで全景へハードカットしていた瞬間</b>（<c>WideStartTime</c>＋0.3秒）。
    /// ①内野ゴロのアウト ②単打 ③失策 ④本塁打（一周の途中と本塁到達）の5枚。</para>
    ///
    /// <para>絵では「引いたな」としか分からないので、<c>FieldFrameDiagForCapture</c> の枠の実寸[m]を
    /// TSV に並べる（全景＝155m。跳んでいれば数字で出る）。</para>
    ///
    /// 出力: screenshots/field-view-cut/*.png ／ out/field-view-cut/frames.tsv
    /// </summary>
    public sealed class FieldViewCutShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/field-view-cut";
        private const string OutDir = "out/field-view-cut";
        private const string SpawnFlag = "KokoSim.FieldViewCutShooter.Spawn";

        private const int MaxGames = 24;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~FieldViewCutAuto").AddComponent<FieldViewCutShooter>();
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
            if (_ctrl == null) { Debug.LogError("[全景カット] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            _log = new StringBuilder("場面\tコマ\t再生時計[s]\t全景開始[s]\t枠の縦[m]\t注視点にいる人[px]\n");
            var want = new HashSet<string> { "内野ゴロ", "単打", "失策", "本塁打" };

            for (var game = 1; game <= MaxGames && want.Count > 0; game++)
            {
                if (game > 1) yield return RestartWithSeed((ulong)(40000 + game * 7717));

                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;
                    var pa = _ctrl.CurrentPa;
                    var play = pa?.Play;
                    var kind = play == null ? null : KindOf(pa.Result, play, want);
                    if (kind == null) { _ctrl.FreezeCurrentAtResult(); yield return null; continue; }

                    want.Remove(kind);
                    var wideStart = CameraPlanBuilder.WideStartTime(play);
                    if (!double.IsFinite(wideStart)) { _ctrl.FreezeCurrentAtResult(); yield return null; continue; }

                    yield return PlayUntil(wideStart + 0.3);
                    yield return Shoot(FileOf(kind) + "-a-was-wide-cut", kind, "①旧カット時刻", wideStart);

                    if (kind == "本塁打")
                    {
                        var touch = PlaybackEvaluator.BatterHomeTouchTime(play) ?? wideStart;
                        yield return PlayUntil((wideStart + touch) * 0.5);
                        yield return Shoot(FileOf(kind) + "-b-circling", kind, "②一周の途中", wideStart);
                        yield return PlayUntil(touch);
                        yield return Shoot(FileOf(kind) + "-c-home", kind, "③本塁到達", wideStart);
                    }
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "frames.tsv"), _log.ToString());
            foreach (var w in want) Debug.LogWarning("[全景カット] 出なかった場面: " + w);
            Debug.Log("[全景カット] DONE → " + ShotDir);
            Time.captureDeltaTime = 0f;
        }

        private static string KindOf(PlateAppearanceResult result, PlaybackPlay play, HashSet<string> want)
        {
            switch (result)
            {
                case PlateAppearanceResult.HomeRun when want.Contains("本塁打"): return "本塁打";
                case PlateAppearanceResult.ReachedOnError when want.Contains("失策"): return "失策";
                case PlateAppearanceResult.Single when want.Contains("単打"): return "単打";
                case PlateAppearanceResult.InPlayOut when want.Contains("内野ゴロ"):
                    foreach (var b in play.Ball)
                    {
                        if (b.ToBase == "first") return "内野ゴロ";   // 一塁送球のある凡打＝狙いの絵
                    }
                    return null;
                default: return null;
            }
        }

        private static string FileOf(string kind) => kind switch
        {
            "内野ゴロ" => "1-grounder",
            "単打" => "2-single",
            "失策" => "3-error",
            _ => "4-homerun",
        };

        private IEnumerator PlayUntil(double t)
        {
            var waited = 0;
            while (waited++ < 3000 && !(_ctrl.StageNameForCapture == "Play" && _ctrl.PlayClockForCapture > 0))
                yield return null;
            var guard = 0;
            while (guard++ < 3000 && _ctrl.PlayClockForCapture < t && _ctrl.StageNameForCapture == "Play")
                yield return null;
        }

        private IEnumerator Shoot(string file, string kind, string frame, double wideStart)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            _ctrl.DuelRigForCapture?.RenderNow();
            yield return null;

            // 枠の実寸は rig の実測（画面に出ている枠そのもの）から採る。TSV の 8列目=枠の縦[m]、
            // 10列目=注視点にいる人の画面px（列は FieldFrameDiagForCapture のコメント参照）。
            var diag = _ctrl.DuelRigForCapture != null
                ? _ctrl.DuelRigForCapture.FieldFrameDiagForCapture((float)_ctrl.PlayClockForCapture)
                : "";
            var cols = diag.Split('\t');
            var frameH = cols.Length > 7 ? cols[7] : "?";
            var aimPx = cols.Length > 9 ? cols[9] : "?";

            _log.Append(kind).Append('\t').Append(frame).Append('\t')
                .Append(_ctrl.PlayClockForCapture.ToString("F2")).Append('\t')
                .Append(wideStart.ToString("F2")).Append('\t')
                .Append(frameH).Append('\t').Append(aimPx).Append('\n');

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

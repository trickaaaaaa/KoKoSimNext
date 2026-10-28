#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】内野ゴロの守備カメラ比較撮影（issue #689）。使い捨て。
    ///
    /// <para>同じシード・同じ打席を2回再生し、<b>現行の追従（案A側）</b>と<b>内野固定枠（案B）</b>を
    /// 同じ4時刻（開始／捕球／送球リリース／決着）で撮る。場面は 6-3・4-3・5-3・併殺。
    /// 枠の縦実寸[m]は TSV に並べる（絵では「引いた」としか分からないため）。</para>
    ///
    /// 出力: screenshots/field-cam-689/{kind}-{mode}-{n}.png ／ out/field-cam-689/frames.tsv
    /// </summary>
    public sealed class FieldCameraCompareShooter : MonoBehaviour
    {
        private const int W = 1280;
        private const int H = 720;
        private const string ShotDir = "screenshots/field-cam-689";
        private const string OutDir = "out/field-cam-689";
        private const string SpawnFlag = "KokoSim.FieldCameraCompareShooter.Spawn";
        private const int MaxGames = 30;
        private const int MaxPlatesPerGame = 200;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~FieldCamCompareAuto").AddComponent<FieldCameraCompareShooter>();
        }

        private MatchLiveController _ctrl;
        private GameObject _liveGo;
        private StringBuilder _log;

        private struct Target { public string Kind; public ulong Seed; public int Plate; public double[] Times; }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            UnityEditor.EditorApplication.isPaused = false;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(OutDir);
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[cam689] MatchLive/Controller が見つからない"); yield break; }
            _liveGo = _ctrl.gameObject;
            yield return null;

            _log = new StringBuilder("場面\t案\tコマ\t再生時計[s]\t枠の縦[m]\tseed\tplate\n");
            var targets = new List<Target>();
            var want = new HashSet<string> { "6-3遊ゴロ", "4-3二ゴロ", "5-3三ゴロ", "併殺" };

            // ---- パス1: 場面を探しながら現行(案A側)で撮る ----
            DuelStageRig.FieldInfieldFixedFrame = false;
            for (var game = 1; game <= MaxGames && want.Count > 0; game++)
            {
                var seed = (ulong)(50000 + game * 7717);
                yield return RestartWithSeed(seed);
                for (var plate = 1; plate <= MaxPlatesPerGame && !_ctrl.IsGameOver && want.Count > 0; plate++)
                {
                    if (!_ctrl.AdvanceForCapture()) break;
                    var pa = _ctrl.CurrentPa;
                    var play = pa?.Play;
                    var kind = play == null ? null : KindOf(pa.Result, play, want, out var times);
                    if (kind == null) { _ctrl.FreezeCurrentAtResult(); yield return null; continue; }
                    want.Remove(kind);
                    KindOf(pa.Result, play, null, out var ts);
                    targets.Add(new Target { Kind = kind, Seed = seed, Plate = plate, Times = ts });
                    yield return ShootSet(kind, "A-current", ts, seed, plate);
                }
            }
            foreach (var w in want) Debug.LogWarning("[cam689] 出なかった場面: " + w);

            // ---- パス2: 同じ打席を折衷（案B＋アウトで寄せる）で撮る ----
            foreach (var tg in targets)
            {
                yield return RestartWithSeed(tg.Seed);
                for (var plate = 1; plate <= tg.Plate && !_ctrl.IsGameOver; plate++)
                {
                    DuelStageRig.FieldInfieldFixedFrame = plate == tg.Plate;
                    if (!_ctrl.AdvanceForCapture()) break;
                    if (plate < tg.Plate) { _ctrl.FreezeCurrentAtResult(); yield return null; continue; }
                    yield return ShootSet(tg.Kind, "C-hybrid", tg.Times, tg.Seed, plate);
                }
            }
            DuelStageRig.FieldInfieldFixedFrame = false;

            File.WriteAllText(Path.Combine(OutDir, "frames.tsv"), _log.ToString());
            Debug.Log("[cam689] DONE → " + ShotDir);
            Time.captureDeltaTime = 0f;
        }

        private IEnumerator ShootSet(string kind, string mode, double[] times, ulong seed, int plate)
        {
            for (var i = 0; i < times.Length; i++)
            {
                yield return PlayUntil(times[i]);
                yield return Shoot(FileOf(kind) + "-" + mode + "-" + (i + 1), kind, mode + "#" + (i + 1), 0, seed, plate);
            }
        }

        /// <summary>場面の判定と撮影時刻（開始／捕球／送球リリース／決着）。</summary>
        private static string KindOf(PlateAppearanceResult result, PlaybackPlay play, HashSet<string> want, out double[] times)
        {
            times = null;
            if (result != PlateAppearanceResult.InPlayOut) return null;
            var segs = play.Ball.OfType<EndpointBallSegment>().ToList();
            var pitch = segs.FirstOrDefault(b => b.Kind == BallSegmentKind.Pitch);
            var roll = segs.FirstOrDefault(b => b.Kind == BallSegmentKind.Roll);
            var th = segs.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
            if (pitch == null || roll == null || th.Count == 0) return null;
            if (segs.Any(b => b.Kind == BallSegmentKind.Flight)) return null;
            string kind = null;
            if (th.Count == 1 && th[0].ToRole == KokoSim.Engine.Match.Field.FieldPosition.FirstBase)
            {
                switch (th[0].FromRole)
                {
                    case KokoSim.Engine.Match.Field.FieldPosition.Shortstop: kind = "6-3遊ゴロ"; break;
                    case KokoSim.Engine.Match.Field.FieldPosition.SecondBase: kind = "4-3二ゴロ"; break;
                    case KokoSim.Engine.Match.Field.FieldPosition.ThirdBase: kind = "5-3三ゴロ"; break;
                }
            }
            else if (th.Count == 2 && th[1].ToRole == KokoSim.Engine.Match.Field.FieldPosition.FirstBase) kind = "併殺";
            if (kind == null) return null;
            if (want != null && !want.Contains(kind)) return null;
            var start = pitch.T1 + 0.5;
            times = new[] { start + 0.15, System.Math.Max(start + 0.3, roll.T1 + 0.2), th[0].T0 + 0.15, th[th.Count - 1].T1 + 0.1 };
            return kind;
        }

        private static string FileOf(string kind) => kind switch
        {
            "6-3遊ゴロ" => "1-ss",
            "4-3二ゴロ" => "2-2b",
            "5-3三ゴロ" => "3-3b",
            _ => "4-dp",
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

        private IEnumerator Shoot(string file, string kind, string frame, double unused, ulong seed, int plate)
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

            _log.Append(kind).Append('\t').Append(file.Contains("-C-") ? "C" : "A").Append('\t').Append(frame).Append('\t')
                .Append(_ctrl.PlayClockForCapture.ToString("F2")).Append('\t')
                .Append(frameH).Append('\t').Append(seed).Append('\t').Append(plate).Append('\n');

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

#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】接触後の打球（＝俯瞰へ渡るまでの一瞬）を**試合の構図のまま**コマ送りで撮る。
    ///
    /// 見たいのは「フライかな？ゴロかな？」が読めるかどうかなので、カメラは確定構図（R-comp60）のまま。
    /// 寄りや真横で撮ると実際にプレイヤーが見る絵と違う結論になる（design-18 §7-1 / DuelSwingStripShooter と同じ理由）。
    ///
    /// 打球は engine の <c>PlaybackSamples</c>（俯瞰モックの実データ）から採る＝自作の弾道で追い込まない。
    /// 出力: screenshots/batted-{fly|grd}-{通し番号}-t{接触からの秒}.png
    /// </summary>
    public sealed class DuelBattedShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int W = 1440;
        private const int H = 810;   // 16:9＝確定構図の縦横比

        private const string SpawnFlag = "KokoSim.DuelBattedShooter.Spawn";

        /// <summary>接触（＝球の到達）までの秒数。コースを渡さない球は既定の飛翔時間で飛ぶ。</summary>
        private const float ContactSec = 0.45f;

        /// <summary>
        /// 接触を0とした撮影時刻[秒]。**負＝接触より前**（スイングそのもの）。
        /// 接触以降だけ撮ると「打球は見えるがスイングは1枚も写っていない」ことに気づけない
        /// （2026-07-31 監督指摘）。ポーズは12fps＝1コマ0.083秒なので、その刻みより細かく採る。
        /// </summary>
        private static readonly float[] Marks =
        {
            -0.40f, -0.33f, -0.25f, -0.17f, -0.08f, 0.00f,
            0.08f, 0.17f, 0.25f, 0.33f, 0.42f, 0.50f,
        };

        /// <summary>絵を撮らずに実測だけ採る刻み[秒]（12fpsのポーズ更新＝0.083sより細かくても増えない）。</summary>
        private const float ProbeStep = 1f / 60f;
        private const float ProbeFrom = -0.45f;
        private const float ProbeTo = 0.60f;

        private DuelStageRig _rig;

        /// <summary>
        /// 太さ・濃さの比較用。<see cref="DuelStageRig.CalibrationOverride"/> は static なので
        /// **Play入りのドメインリロードで消える**（0.09と0.045で絵が完全に同一になり気づいた。2026-07-31）。
        /// リロードを跨ぐ EditorPrefs で渡す。負なら既定のまま。
        /// </summary>
        private const string WidthKey = "KokoSim.DuelBattedShooter.WidthM";
        private const string AlphaKey = "KokoSim.DuelBattedShooter.Alpha";
        /// <summary>スイング面のつまみ（傾き・持ち上げ・バット単体）を全部0にして掃く＝原因の切り分け用。</summary>
        private const string NoPlaneKey = "KokoSim.DuelBattedShooter.NoPlane";
        /// <summary>再生速度の上書き。遅くするとポーズ（12fps）の刻みが細かくなり、クリップを細かく掃ける。</summary>
        private const string SpeedKey = "KokoSim.DuelBattedShooter.Speed";

        public static void Arm(float widthM = -1f, float alpha = -1f, bool noSwingPlane = false,
            float swingSpeed = -1f)
        {
            UnityEditor.EditorPrefs.SetFloat(WidthKey, widthM);
            UnityEditor.EditorPrefs.SetFloat(AlphaKey, alpha);
            UnityEditor.EditorPrefs.SetBool(NoPlaneKey, noSwingPlane);
            UnityEditor.EditorPrefs.SetFloat(SpeedKey, swingSpeed);
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~DuelBattedAuto").AddComponent<DuelBattedShooter>();
        }

        /// <summary>
        /// 【開発用】打撃フォーム確認窓と同じ条件（Edit mode・球なし・クリップを直接送る）で、
        /// 指定したクリップ内時刻のバットを実測する。窓の「コマ ▶」で見えている絵と同じものを数値で出す。
        /// </summary>
        /// <summary>【切り分け用】打者の立ち位置と接触の時刻を差し替えて掃く（負＝既定）。</summary>
        public static string ProbeSeek(string csvTimes, float batterX, float anchor, float reach, int view)
        {
            var cal = DuelCalibration.Default;
            if (batterX < 0f) cal.BatterX = batterX;
            DuelStageRig.CalibrationOverride = cal;
            DuelStageRig.SwingAnchorOverrideForCapture = anchor;
            DuelStageRig.SweepReachOverrideForCapture = reach;
            var s = ProbeSeek(csvTimes);
            if (view >= 0) s += ShootSeek(csvTimes, view) + "\n";
            DuelStageRig.CalibrationOverride = null;
            DuelStageRig.SwingAnchorOverrideForCapture = -1f;
            DuelStageRig.SweepReachOverrideForCapture = -1f;
            return s;
        }

        public static string ProbeSeek(string csvTimes)
        {
            var rig = new DuelStageRig();
            if (!rig.Available) return "rig が組めない";
            // 窓と同じ手順（「フライ」を押してから「コマ ▶」で送る）。押さずに送ると打者は構えループの
            // ままで、クリップ内時刻を動かしてもポーズが変わらない＝窓の再現にならない
            // 構えを十分に回してから振る。バットの向きは**構えループで焼く**ので、湧かせた直後に
            // 振ると焼き付け元が落ち着く前のポーズになり、トップの軸そのものが別物になる
            for (int i = 0; i < 90; i++) rig.Tick(1f / 60f);
            rig.OnPitchWindup();
            for (int i = 0; i < 6; i++) rig.Tick(1f / 60f);
            rig.OnPitchReleased(0f, DuelPitchOutcome.InPlayContact, null, RealBatted("フライ"));
            var sb = new System.Text.StringBuilder();
            foreach (var part in csvTimes.Split(','))
            {
                float t;
                if (!float.TryParse(part.Trim(), out t)) continue;
                rig.SeekBatter(t);
                sb.Append(rig.BatContactDiagForCapture())
                  .Append(" | ").Append(rig.BatDiagForCapture()).Append('\n');
            }
            rig.Dispose();
            return sb.ToString();
        }

        /// <summary>
        /// 【開発用】<see cref="ProbeSeek"/> と同じ条件のコマを**絵で**出す。窓の「真横から」と同じ
        /// 狙い方（腰の高さを中心に、投手線と直交する向きから2.1m四方）＝監督が見ている絵と同じ。
        /// </summary>
        public static string ShootSeek(string csvTimes, int view)
        {
            Directory.CreateDirectory(Dir);
            var rig = new DuelStageRig();
            if (!rig.Available) return "rig が組めない";
            rig.EnsureSize(960, 720);
            for (int i = 0; i < 90; i++) rig.Tick(1f / 60f);
            rig.OnPitchWindup();
            for (int i = 0; i < 6; i++) rig.Tick(1f / 60f);
            rig.OnPitchReleased(0f, DuelPitchOutcome.InPlayContact, null, RealBatted("フライ"));
            int n = 0;
            foreach (var part in csvTimes.Split(','))
            {
                float t;
                if (!float.TryParse(part.Trim(), out t)) continue;
                rig.SeekBatter(t);
                Aim(rig, view);
                rig.RenderNow();
                Save(rig.Target, string.Format("seek-v{0}-{1:00}-c{2:F3}", view, n++, t));
            }
            rig.Dispose();
            return "ok " + n + "枚";
        }

        /// <summary>打撃フォーム確認窓 <c>AimView</c> の複製（0=投手側 / 1=真横 / 2=反対の横）。</summary>
        private static void Aim(DuelStageRig rig, int view)
        {
            var anim = rig.BatterAnimator;
            var root = rig.StageRoot;
            if (anim == null || root == null) return;
            var hipsT = anim.GetBoneTransform(HumanBodyBones.Hips);
            var hips = hipsT != null ? hipsT.position : root.position;
            var center = hips + Vector3.up * 0.15f;
            var toP = rig.PitcherHeadPoint - hips;
            toP.y = 0f;
            toP = toP.sqrMagnitude > 1e-6f ? toP.normalized : Vector3.forward;
            var side = Vector3.Cross(Vector3.up, toP).normalized;
            var dir = view == 1 ? side : view == 2 ? -side : toP;
            const float radius = 3.2f, frameH = 2.1f;
            var cam = center + dir * radius + Vector3.up * (frameH * 0.06f);
            rig.FramePreview(root.InverseTransformPoint(cam), root.InverseTransformPoint(center), frameH);
        }

        private static void Save(RenderTexture rt, string name)
        {
            if (rt == null) return;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            float w = UnityEditor.EditorPrefs.GetFloat(WidthKey, -1f);
            float a = UnityEditor.EditorPrefs.GetFloat(AlphaKey, -1f);
            bool noPlane = UnityEditor.EditorPrefs.GetBool(NoPlaneKey, false);
            float sp = UnityEditor.EditorPrefs.GetFloat(SpeedKey, -1f);
            if (w > 0f || a > 0f || noPlane || sp > 0f)
            {
                var cal = DuelCalibration.Default;
                if (w > 0f) cal.BattedTrailWidthM = w;
                if (a > 0f) cal.BattedTrailAlpha = a;
                if (sp > 0f) cal.SwingSpeed = sp;
                if (noPlane) { cal.SwingPlaneTiltDeg = 0f; cal.SwingLiftM = 0f; cal.SwingPlaneBatOnly = 0f; }
                DuelStageRig.CalibrationOverride = cal;
                Debug.Log("[Batted] 太さ=" + cal.BattedTrailWidthM + "m 濃さ=" + cal.BattedTrailAlpha
                    + " 速度=" + cal.SwingSpeed + " スイング面=" + (noPlane ? "切" : "既定"));
            }

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[Batted] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            yield return Probe(RealBatted("フライ"));
            yield return Shoot("fly", RealBatted("フライ"));
            yield return Shoot("grd", RealBatted("ゴロ"));
            Debug.Log("[Batted] DONE");
        }

        /// <summary>
        /// 絵を撮らずに「バットの芯が打点へ最接近するのは接触の何秒後か」を掃く。
        /// アンカーがずれていても絵では「当たったように見えない」としか分からないので数値で出す。
        /// </summary>
        private IEnumerator Probe(KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment seg)
        {
            if (seg == null) yield break;
            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, DuelPitchOutcome.InPlayContact, null, seg);

            float t = 0f, best = 999f, bestAt = 0f;
            var sb = new System.Text.StringBuilder("[Batted] 掃引 接触からの秒\tclipT\t芯→打点[m]\n");
            string lastLine = null;
            while (t < ContactSec + ProbeTo)
            {
                _rig.Tick(ProbeStep); t += ProbeStep;
                if (t < ContactSec + ProbeFrom) continue;
                var line = _rig.BatContactDiagForCapture();
                if (line == lastLine) continue;      // 12fps＝ポーズが変わった時だけ記録する
                lastLine = line;
                float rel = t - ContactSec;
                sb.Append((rel >= 0 ? "+" : "")).Append(rel.ToString("F2")).Append('\t').Append(line)
                  .Append("\n\t\t").Append(_rig.BatterRootDiagForCapture()).Append('\n');
                int i0 = line.IndexOf("打点まで=");
                if (i0 >= 0)
                {
                    float d;
                    if (float.TryParse(line.Substring(i0 + 5, 5), out d) && d < best) { best = d; bestAt = rel; }
                }
                yield return null;
            }
            sb.Append("最接近 ").Append(best.ToString("F3")).Append("m @ 接触")
              .Append(bestAt >= 0 ? "+" : "").Append(bestAt.ToString("F2")).Append("s");
            Debug.Log(sb.ToString());
            _rig.HideBall();
            for (int i = 0; i < 40; i++) { _rig.Tick(1f / 60f); yield return null; }
        }

        private IEnumerator Shoot(string prefix,
            KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment seg)
        {
            if (seg == null) { Debug.LogError("[Batted] 打球が見つからない: " + prefix); yield break; }

            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, DuelPitchOutcome.InPlayContact, null, seg);

            float t = 0f;
            for (int f = 0; f < Marks.Length; f++)
            {
                float mark = ContactSec + Marks[f];
                while (t < mark) { _rig.Tick(1f / 60f); t += 1f / 60f; yield return null; }
                Debug.Log(string.Format("[Batted] {0} f={1:00} t={2:+0.00;-0.00} clip={3} {4}",
                    prefix, f, Marks[f], _rig.BatterClock.ToString("F2"), _rig.BattedDiagForCapture()));
                yield return Capture(string.Format("batted-{0}-{1:00}-t{2:+0.00;-0.00}", prefix, f, Marks[f]));
            }
            _rig.HideBall();
            for (int i = 0; i < 30; i++) { _rig.Tick(1f / 60f); yield return null; }
        }

        /// <summary>
        /// **実試合を1つ回して**その打球セグメントを採る。<c>PlaybackSamples</c>（俯瞰モックの見本）は
        /// ArcSegment/RollSegment で、実データの <c>EndpointBallSegment</c> とは高さプロファイルが違う
        /// （見本は打点1.0mから始まり、実データは h(0)=0 から4A·u(1−u)で立ち上がる）。
        /// 打点合わせの補正はまさにその差を吸収する所なので、見本で確かめると本番を確かめたことにならない。
        /// </summary>
        private static KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment RealBatted(string resultContains)
        {
            var rng = new KokoSim.Engine.Core.Xoshiro256Random(20260731UL);
            var away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(58, "撮影A", rng);
            var home = KokoSim.Engine.Nation.StrengthTeamFactory.Create(58, "撮影B",
                new KokoSim.Engine.Core.Xoshiro256Random(20260731UL ^ 0xABCDUL));
            var ctx = new KokoSim.Engine.Match.Game.GameContext { CaptureTimelines = true };
            var prog = new KokoSim.Engine.Match.Timeline.Playback.MatchProgression(away, home, ctx, 20260731UL);
            int guard = 0;
            while (guard++ < 400 && prog.Advance())
            {
                var pa = prog.Current;
                var play = pa?.Play;
                if (play == null || play.Result == null || !play.Result.Contains(resultContains)) continue;
                foreach (var s in play.Ball)
                {
                    if (s is KokoSim.Engine.Match.Timeline.Playback.PitchSegment) continue;
                    if (s is KokoSim.Engine.Match.Timeline.Playback.EndpointBallSegment eb
                        && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Pitch) continue;
                    Debug.Log("[Batted] 採用: " + play.Result + " 種別="
                        + (s as KokoSim.Engine.Match.Timeline.Playback.EndpointBallSegment)?.Kind
                        + " 長さ=" + (s.T1 - s.T0).ToString("F2") + "s");
                    return s;
                }
            }
            return null;
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = _rig.Target;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }
    }
}
#endif

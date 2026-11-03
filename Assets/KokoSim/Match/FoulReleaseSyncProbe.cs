#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// ファウルの次の球で投手のリリースと球の発射が合っているかの実測（issue #770）。
    /// ファウルを含む打席を対決ビューで頭から再生し、リリースごとの「投手クリップ時刻 − 発射時刻」
    /// （<see cref="DuelStageRig.ReleaseDriftsForCapture"/>）を書き出す。12fps量子化ぶん（±0.09s）以内なら合っている。
    /// <see cref="MatchLiveController.FoulReleaseShiftDisabledForCapture"/> を真にすると修正前の挙動で測れる。
    /// </summary>
    public sealed class FoulReleaseSyncProbe : MonoBehaviour
    {
        /// <summary>出力先。Play入りのドメインリロードで static は初期化子へ戻るので、ここを書き換えて使う。</summary>
        public static string OutPath =
            "/private/tmp/claude-501/-Users-seiya-oda-Unity-KoKoSimNext/26723dd3-763a-4a82-a0bf-61d98bc986f5/scratchpad/foul-release.tsv";

        /// <summary>測る打席数（ファウルが打席の途中にある打席だけ数える）。</summary>
        public static int PaCount = 6;

        /// <summary>真＝修正前の挙動で測る（Edit mode で設定＝シリアライズされるので Play 入りを跨げる）。</summary>
        public bool DisableShift;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            MatchLiveController.FoulReleaseShiftDisabledForCapture = DisableShift;
            if (DisableShift) OutPath = OutPath.Replace(".tsv", "-before.tsv");
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            for (int i = 0; i < 180; i++) yield return null;
            EnsureOnlyMatchLive();
            yield return null;

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[FoulRel] MatchLive/Controller が見つからない"); yield break; }
            var router = KokoSim.Unity.Shell.ScreenRouter.Instance;
            if (router != null) router.enabled = false;

            var log = new StringBuilder("# shiftDisabled=" + MatchLiveController.FoulReleaseShiftDisabledForCapture + "\n");
            log.Append("pa\tkinds\tdrifts(投手クリップ−発射時刻, 秒)\n");
            int measured = 0, guard = 0;
            var trace = new StringBuilder("pa\tt\tpitchClock\treleases\tpClipT\tpYaw\tpDx\tpDz\tpRootM\tpFade\n");
            while (measured < PaCount && guard++ < 400 && ctrl.AdvanceForCapture())
            {
                var pitches = ctrl.CurrentPa?.PitchSeq?.Pitches;
                if (pitches == null || pitches.Count < 2) continue;
                bool midFoul = false;
                var kinds = new List<string>();
                for (int i = 0; i < pitches.Count; i++)
                {
                    kinds.Add(pitches[i].Kind.ToString());
                    if (i < pitches.Count - 1 && pitches[i].Kind == KokoSim.Engine.Match.AtBat.PitchKind.Foul) midFoul = true;
                }
                if (!midFoul) continue;

                if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
                var rig = ctrl.DuelRigForCapture;
                if (rig == null || !rig.Available) { Debug.LogError("[FoulRel] DuelStageRig が使えない"); yield break; }
                ctrl.RestartCurrentPaForCapture();
                rig.ReleaseDriftsForCapture.Clear();
                float t = 0f;
                float maxHold = 0f;
                var clockField = typeof(MatchLiveController).GetField("_pitchClock",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var holdField = typeof(MatchLiveController).GetField("_foulFlightHoldSeconds",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                while (rig.ReleaseDriftsForCapture.Count < pitches.Count && t < 60f)
                {
                    yield return null;
                    t += Time.deltaTime;
                    if (holdField?.GetValue(ctrl) is float h && h > maxHold) maxHold = h;
                    trace.Append(measured).Append('\t').Append(t.ToString("F2")).Append('\t')
                        .Append(((float)clockField.GetValue(ctrl)).ToString("F2")).Append('\t')
                        .Append(rig.ReleaseDriftsForCapture.Count).Append('\t')
                        .Append(rig.PitcherDiagForCapture()).Append('\n');
                }
                log.Append("# foulHoldMax=").Append(maxHold.ToString("F2")).Append(" elapsed=").Append(t.ToString("F1")).Append('\n');
                log.Append(measured).Append('\t').Append(string.Join(",", kinds)).Append('\t');
                foreach (var d in rig.ReleaseDriftsForCapture) log.Append(d.ToString("F2")).Append(' ');
                log.Append('\n');
                measured++;
            }
            File.WriteAllText(OutPath, log.ToString());
            File.WriteAllText(OutPath.Replace(".tsv", "-trace.tsv"), trace.ToString());
            Debug.Log("[FoulRel] DONE measured=" + measured + " out=" + OutPath + "\n" + log);
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

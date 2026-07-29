#if UNITY_EDITOR
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】空振りフォロースルー中のバット可視状態ダンプ（AlignBat診断）。
    /// DuelViewShooter と同じ手順で空振り打席を頭出しし、スイング前後のバット transform と
    /// 両手（手首・掌中心）位置を1本のログに集めて出す。確認が済んだら削除してよい。
    /// </summary>
    public sealed class DuelBatProbe : MonoBehaviour
    {
        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[BatProbe] MatchLive/Controller が見つからない"); yield break; }
            yield return null;

            int targetIdx = -1;
            int guard = 0;
            while (guard++ < 60 && ctrl.AdvanceForCapture())
            {
                var pitches = ctrl.CurrentPa?.PitchSeq?.Pitches;
                if (pitches == null) continue;
                for (int i = 0; i < pitches.Count; i++)
                {
                    if (pitches[i].Kind == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike) { targetIdx = i; break; }
                }
                if (targetIdx >= 0) break;
            }
            if (targetIdx < 0) { Debug.LogError("[BatProbe] 空振りを含む打席が見つからない"); yield break; }
            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            ctrl.RestartCurrentPaForCapture();

            GameObject bat = null;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go.name == "prop-bat" && go.scene.IsValid()) { bat = go; break; }

            float swingAt = DuelStageRig.WindupSeconds + targetIdx * DuelStageRig.DefaultPitchInterval + 0.45f;
            var sb = new StringBuilder();
            sb.Append("swingAt=").Append(swingAt.ToString("F2")).Append(" bat=").Append(bat != null).Append('\n');
            float t = 0f;
            int frame = 0;
            while (t < swingAt + 2.0f)
            {
                t += Time.deltaTime;
                frame++;
                if (t < swingAt - 0.6f || (frame % 6) != 0) { yield return null; continue; }

                sb.Append("t=").Append((t - swingAt).ToString("F2"));
                if (bat != null)
                {
                    sb.Append(" act=").Append(bat.activeInHierarchy ? 1 : 0);
                    sb.Append(" pos=").Append(bat.transform.position.ToString("F2"));
                    sb.Append(" axis=").Append((bat.transform.rotation * Vector3.up).ToString("F2"));
                    sb.Append(" scl=").Append(bat.transform.lossyScale.ToString("F2"));
                }
                foreach (var a in FindObjectsOfType<Animator>())
                {
                    var lh = a.GetBoneTransform(HumanBodyBones.LeftHand);
                    var rh = a.GetBoneTransform(HumanBodyBones.RightHand);
                    if (lh == null || rh == null) continue;
                    if (bat != null && Vector3.Distance(lh.position, bat.transform.position) > 2f) continue;
                    sb.Append(" | ").Append(a.gameObject.name)
                      .Append(" L=").Append(lh.position.ToString("F2"))
                      .Append(" R=").Append(rh.position.ToString("F2"))
                      .Append(" gap=").Append(Vector3.Distance(lh.position, rh.position).ToString("F2"));
                }
                sb.Append('\n');
                yield return null;
            }
            Debug.Log("[BatProbe]\n" + sb);
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

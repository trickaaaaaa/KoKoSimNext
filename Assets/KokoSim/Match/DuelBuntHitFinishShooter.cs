#if UNITY_EDITOR
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #1141: バントを当てた後の打者が走り出して映らないかを実測する。
    /// 右打者・左打者 × 打ち切り時刻（旧=スイングと同じ 1.433 / 新=既定）× インプレー・ファウルで、
    /// バント構え→当てる→2.2秒を 24fps で回し、毎コマ腰のステージローカルXZ（+x=一塁側）を TSV へ書く。
    /// 要所は PNG も撮る。
    /// </summary>
    public sealed class DuelBuntHitFinishShooter : MonoBehaviour
    {
        public static string Dir = "screenshots/issue-1141";
        private const int W = 960;
        private const int H = 540;
        private const float Step = 1f / 24f;
        private static readonly float[] ShotTimes = { 0.2f, 0.4f, 0.6f, 0.9f, 1.3f };

        private DuelStageRig _rig;
        private StringBuilder _tsv;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            _tsv = new StringBuilder("finish\tside\toutcome\tt\tdiag\n");

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[BuntHitFinish] rig not available"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            foreach (var finish in new[] { 1.433f, -1f })
            foreach (var left in new[] { false, true })
            foreach (var outcome in new[] { DuelPitchOutcome.InPlayContact, DuelPitchOutcome.FoulContact })
            {
                DuelStageRig.BuntHitFinishOverrideForCapture = finish;
                string tag = (finish > 0f ? "old" : "new") + "-" + (left ? "L" : "R")
                             + "-" + (outcome == DuelPitchOutcome.InPlayContact ? "inplay" : "foul");
                _rig.SetHandedness(left, false);
                _rig.PrepareBatterBunt(false);
                yield return Run(tag, 0.5f, null);
                _rig.PrepareBatterBunt(true, 2.0f);
                yield return Run(tag, 3.2f, null);
                _rig.OnPitchWindup();
                yield return Run(tag, 4f / 60f, null);
                _rig.OnPitchReleased(0f, outcome, null, null, isBuntAttempt: true);
                yield return Run(tag, 2.2f, tag);
            }
            DuelStageRig.BuntHitFinishOverrideForCapture = -1f;
            File.WriteAllText(Path.Combine(Dir, "bunt-hit-finish.tsv"), _tsv.ToString());
            Debug.Log("[BuntHitFinish] DONE");
        }

        private void OnDisable()
        {
            DuelStageRig.BuntHitFinishOverrideForCapture = -1f;
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private IEnumerator Run(string tag, float seconds, string shotTag)
        {
            float t = 0f;
            int next = 0;
            while (t < seconds - 1e-4f)
            {
                _rig.Tick(Step); t += Step;
                if (shotTag != null)
                {
                    _tsv.Append(tag).Append('\t').Append(t.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(_rig.BatterRootDiagForCapture()).Append('\n');
                    if (next < ShotTimes.Length && t >= ShotTimes[next] - 1e-4f)
                    {
                        yield return Shot(tag + "-t" + ShotTimes[next].ToString("F1", CultureInfo.InvariantCulture));
                        next++;
                    }
                }
                yield return null;
            }
        }

        private IEnumerator Shot(string name)
        {
            _rig.Tick(0f);
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

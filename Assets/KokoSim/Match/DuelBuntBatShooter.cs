#if UNITY_EDITOR
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #938: バント構えのバットが上下に揺れないかを実測する。
    /// 右打者・左打者 × 修正前（水平固定を切る）/修正後 で、通常の構え→バント構えへの移行→構え保持→
    /// バント（当てる/空振り）→通常の構えへ戻る、を 24fps で回し、毎コマのバット軸の仰角を TSV に書く。
    /// 要所は PNG も撮る。
    /// </summary>
    public sealed class DuelBuntBatShooter : MonoBehaviour
    {
        public static string Dir = "screenshots/issue-938";
        private const int W = 960;
        private const int H = 540;
        private const float Step = 1f / 24f;

        private DuelStageRig _rig;
        private StringBuilder _tsv;
        private string _tag;
        private float _t;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            _tsv = new StringBuilder("mode\tside\tphase\tt\televDeg\taxisX\taxisY\taxisZ\n");

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[BuntBat] rig not available"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            foreach (var fixedOn in new[] { false, true })
            foreach (var left in new[] { false, true })
            {
                DuelStageRig.BuntFlatDisabledForCapture = !fixedOn;
                _tag = (fixedOn ? "after" : "before") + "\t" + (left ? "L" : "R");
                string png = (fixedOn ? "after" : "before") + "-" + (left ? "L" : "R");
                _rig.SetHandedness(left, false);
                _rig.PrepareBatterBunt(false);
                yield return Run("normal", 1.0f);
                yield return Shot(png + "-0-normal");

                _rig.PrepareBatterBunt(true, 2.0f);
                yield return Run("into", 1.2f);
                yield return Run("hold", 3.0f);
                yield return Shot(png + "-1-hold");

                // 当てる
                _rig.OnPitchWindup();
                yield return Run("windup", 4f / 60f);
                _rig.OnPitchReleased(0f, DuelPitchOutcome.FoulContact, null, null, isBuntAttempt: true);
                yield return Run("hit", 0.45f);
                yield return Shot(png + "-2-hit");
                yield return Run("hit", 1.0f);

                // 空振り（構えを予約し直してから）
                _rig.PrepareBatterBunt(true, 2.0f);
                yield return Run("into2", 2.0f);
                _rig.OnPitchWindup();
                yield return Run("windup2", 4f / 60f);
                _rig.OnPitchReleased(0f, DuelPitchOutcome.SwingMiss, null, null, isBuntAttempt: true);
                yield return Run("miss", 1.4f);

                // 通常の構えへ戻る
                _rig.PrepareBatterBunt(false);
                yield return Run("back", 1.5f);
                yield return Shot(png + "-3-back");

                // 通常スイング（回帰）
                _rig.OnPitchWindup();
                yield return Run("swwindup", 4f / 60f);
                _rig.OnPitchReleased(0f, DuelPitchOutcome.SwingMiss);
                yield return Run("swing", 1.2f);
            }
            DuelStageRig.BuntFlatDisabledForCapture = false;
            File.WriteAllText(Path.Combine(Dir, "bat-elev.tsv"), _tsv.ToString());
            Debug.Log("[BuntBat] DONE");
        }

        private void OnDisable()
        {
            DuelStageRig.BuntFlatDisabledForCapture = false;
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private IEnumerator Run(string phase, float seconds)
        {
            float t = 0f;
            while (t < seconds - 1e-4f)
            {
                _rig.Tick(Step); t += Step; _t += Step;
                var a = _rig.BatAxisForCapture;
                float elev = a == Vector3.zero ? float.NaN : Mathf.Asin(Mathf.Clamp(a.y, -1f, 1f)) * Mathf.Rad2Deg;
                _tsv.Append(_tag).Append('\t').Append(phase).Append('\t')
                    .Append(_t.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(elev.ToString("F2", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(a.x.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(a.y.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(a.z.ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
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

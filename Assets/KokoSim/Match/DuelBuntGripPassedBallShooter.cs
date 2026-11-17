#if UNITY_EDITOR
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #1087（バントの両手）と #1088（後逸の動き出し）の見比べ。
    /// 試合を通さず対決ビューの組み立てだけを動かす（<see cref="DuelBuntBatShooter"/> と同じ作法）。
    ///
    /// <para>#1087: 右打者・左打者 × 旧（握りを詰める）/新 で、通常の構え→バント構え→当てる→構え直し→空振り→通常の構え
    /// を 24fps で回し、毎コマのバット中心・軸を TSV に書く（1コマの飛びを数で見る）。要所は PNG。</para>
    /// <para>#1088: 後逸クリップ4本（L / L_S / R / R_S）を選ぶ着弾位置へ捕れない球を投げ、旧/新 で
    /// リリース〜到達後までを数コマ撮る。</para>
    /// 出力: screenshots/issue-1087-1088/
    /// </summary>
    public sealed class DuelBuntGripPassedBallShooter : MonoBehaviour
    {
        public static string Dir = "screenshots/issue-1087-1088";
        private const int W = 960;
        private const int H = 540;
        private const float Step = 1f / 24f;
        /// <summary>撮る後逸の飛翔[s]（実試合の速球の飛翔はおよそ0.42〜0.48秒）。</summary>
        public static float Flight = 0.45f;
        /// <summary>撮る時刻（リリースからの秒）。</summary>
        public static float[] PassedShots = { 0.05f, 0.20f, 0.30f, 0.40f, 0.45f, 0.60f };

        private DuelStageRig _rig;
        private StringBuilder _tsv;
        private string _tag;
        private float _t;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            _tsv = new StringBuilder("mode\tside\tphase\tt\tcx\tcy\tcz\tax\tay\taz\n");
            var diag = new StringBuilder();

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[1087-1088] rig not available"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            // ---- #1087 バントの両手 ----
            foreach (var oldOn in new[] { true, false })
            foreach (var left in new[] { false, true })
            {
                DuelStageRig.BuntGripOldForCapture = oldOn;
                string mode = oldOn ? "old" : "new";
                _tag = mode + "\t" + (left ? "L" : "R");
                string png = "grip-" + mode + "-" + (left ? "L" : "R");
                _rig.SetHandedness(left, false);
                _rig.PrepareBatterBunt(false);
                yield return Run("normal", 1.0f);

                _rig.PrepareBatterBunt(true, 2.0f);
                yield return Run("into", 1.2f);
                yield return Run("hold", 2.0f);
                yield return Shot(png + "-1-hold");

                _rig.OnPitchWindup();
                yield return Run("windup", 4f / 60f);
                _rig.OnPitchReleased(0f, DuelPitchOutcome.FoulContact, null, null, isBuntAttempt: true);
                yield return Run("hit", 0.45f);
                yield return Shot(png + "-2-hit");
                yield return Run("hit", 0.25f);
                yield return Shot(png + "-2b-run");
                yield return Run("hit", 0.25f);
                yield return Shot(png + "-2c-run");
                yield return Run("hit", 0.5f);

                _rig.PrepareBatterBunt(true, 2.0f);
                yield return Run("into2", 2.0f);
                _rig.OnPitchWindup();
                yield return Run("windup2", 4f / 60f);
                _rig.OnPitchReleased(0f, DuelPitchOutcome.SwingMiss, null, null, isBuntAttempt: true);
                yield return Run("miss", 0.45f);
                yield return Shot(png + "-3-miss");
                yield return Run("miss", 1.0f);

                _rig.PrepareBatterBunt(false);
                yield return Run("back", 1.5f);
                yield return Shot(png + "-4-back");

                // 通常スイング（回帰。握りの補正が変わっていないこと）
                _rig.OnPitchWindup();
                yield return Run("swwindup", 4f / 60f);
                _rig.OnPitchReleased(0f, DuelPitchOutcome.SwingMiss);
                yield return Run("swing", 0.40f);
                yield return Shot(png + "-5-swing");
                yield return Run("swing", 1.0f);
            }
            DuelStageRig.BuntGripOldForCapture = false;
            File.WriteAllText(Path.Combine(Dir, "bat.tsv"), _tsv.ToString());

            // ---- #1088 後逸の動き出し ----
            _rig.SetHandedness(false, false);
            _rig.PrepareBatterBunt(false);
            yield return Run("reset", 1.0f);
            // 各後逸クリップの「伸び切り位置」（PassedBallAnchors.Mitt）に着弾させて、そのクリップを選ばせる
            var targets = new[]
            {
                ("L", -0.42f, 0.19f), ("L_S", -0.64f, 0.81f), ("R", 0.22f, 0.59f), ("R_S", 0.17f, 0.89f),
            };
            foreach (var oldOn in new[] { true, false })
            foreach (var (name, px, py) in targets)
            {
                DuelStageRig.PassedBallOldForCapture = oldOn;
                string mode = oldOn ? "old" : "new";
                _rig.OnPitchWindup();
                yield return Run("pbwindup", 1.3f);
                var pitch = new DuelPitchFlight(px, py, 0f, 0.75f, Flight, 0f, 0f, 135f);
                _rig.OnPitchReleased(0f, DuelPitchOutcome.NotCaught, pitch);
                float t = 0f;
                int shot = 0;
                diag.Append(mode).Append(' ').Append(name).Append(": ").Append(_rig.CatchDiagForCapture()).Append('\n');
                while (shot < PassedShots.Length)
                {
                    if (t >= PassedShots[shot] - 1e-4f)
                    {
                        yield return Shot("pb-" + mode + "-" + name + "-t" + PassedShots[shot].ToString("0.00", CultureInfo.InvariantCulture));
                        diag.Append("  t=").Append(t.ToString("F2", CultureInfo.InvariantCulture)).Append(' ')
                            .Append(_rig.CatchDiagForCapture()).Append('\n');
                        shot++;
                        continue;
                    }
                    _rig.Tick(Step); t += Step;
                    yield return null;
                }
                yield return Run("pbafter", 2.0f);
            }
            DuelStageRig.PassedBallOldForCapture = false;
            File.WriteAllText(Path.Combine(Dir, "passed-ball.txt"), diag.ToString());
            File.WriteAllText(Path.Combine(Dir, "DONE"), "ok");
            Debug.Log("[1087-1088] DONE");
        }

        private void OnDisable()
        {
            DuelStageRig.BuntGripOldForCapture = false;
            DuelStageRig.PassedBallOldForCapture = false;
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private IEnumerator Run(string phase, float seconds)
        {
            float t = 0f;
            while (t < seconds - 1e-4f)
            {
                _rig.Tick(Step); t += Step; _t += Step;
                var c = _rig.BatCenterForCapture;
                var a = _rig.BatAxisForCapture;
                _tsv.Append(_tag).Append('\t').Append(phase).Append('\t')
                    .Append(_t.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(c.x.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(c.y.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(c.z.ToString("F3", CultureInfo.InvariantCulture)).Append('\t')
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

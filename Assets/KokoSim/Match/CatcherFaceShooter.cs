#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #804（顔を背ける捕球モーション）の確認撮影。
    /// 5方向（真ん中/高め/低め/捕手から見て左/右）の見逃しを流し、捕手の顔を正面寄りから連番で撮る。
    /// 各球で選ばれた捕球クリップと、捕球の瞬間のミットずれをログへ残す。
    /// </summary>
    public sealed class CatcherFaceShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/catcher-face-804";
        private const int W = 960;
        private const int H = 720;

        private DuelStageRig _rig;

        /// <summary>true＝中継視点（試合の構図）だけを撮る。Play中に代入してから付けること。</summary>
        public static bool TvOnly;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[i804] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            yield return Run("C-center", 0.00f, 0.78f);
            yield return Run("U-high", 0.00f, 1.15f);
            yield return Run("D-low", 0.00f, 0.32f);
            yield return Run("L-left", -0.45f, 0.70f);
            yield return Run("R-right", 0.40f, 0.68f);
            // R を外した後の受け持ち確認（捕手から見て右へ外れる球を左右・高さで振る）
            yield return Run("R2-right-near", 0.25f, 0.70f);
            yield return Run("R3-right-far", 0.55f, 0.68f);
            yield return Run("R4-right-high", 0.40f, 1.00f);
            yield return Run("R5-right-low", 0.40f, 0.40f);

            Debug.Log("[i804] DONE → " + Dir);
        }

        private IEnumerator Run(string tag, float x, float y)
        {
            _rig.ResetForNewPlateAppearance();
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchWindup();
            float t = 0f;
            while (t < DuelStageRig.WindupSeconds) { _rig.Tick(1f / 60f); t += 1f / 60f; yield return null; }

            const float flight = 0.45f;
            _rig.OnPitchReleased(-1f, DuelPitchOutcome.Take, new DuelPitchFlight(x, y, x, y, flight, 0.35f, 0f));
            Debug.Log("[i804] === " + tag + " / " + _rig.CatchDiagForCapture());

            // 捕球の前後: −0.10 / 0 / +0.15 / +0.30 / +0.45 / +0.60 秒
            float[] marks = { flight - 0.10f, flight, flight + 0.15f, flight + 0.30f, flight + 0.45f, flight + 0.60f };
            float minDev = float.MaxValue, closest = float.MaxValue;
            t = 0f;
            for (int m = 0; m < marks.Length; m++)
            {
                while (t < marks[m])
                {
                    _rig.Tick(1f / 60f); t += 1f / 60f;
                    float bm = _rig.BallToMittMeters();
                    if (bm >= 0f && bm < closest) closest = bm;
                    var mm = System.Text.RegularExpressions.Regex.Match(_rig.CatchDiagForCapture(), @"ずれ=([0-9.]+)m");
                    if (mm.Success)
                    {
                        float dev = float.Parse(mm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        if (dev < minDev) minDev = dev;
                    }
                    yield return null;
                }
                if (TvOnly) { yield return Shot(tag + "-f" + m + "-tv", null); continue; }   // 中継視点（対決ビュー本来の構図）
                yield return Shot(tag + "-f" + m + "-front", Front);
                yield return Shot(tag + "-f" + m + "-side", Side);
            }
            Debug.Log("[i804] " + tag + " 最小ずれ=" + minDev.ToString("F3") + "m 球とミット最接近=" + closest.ToString("F3") + "m / " + _rig.CatchDiagForCapture());
        }

        // 投手側やや一塁寄りから捕手の顔を正面に（右打者は三塁側なので一塁寄りなら被らない）
        private static readonly (Vector3 cam, Vector3 look, float h) Front =
            (new Vector3(1.0f, 1.0f, 2.2f), new Vector3(0f, 0.75f, -0.6f), 1.6f);
        // 一塁側の真横＝うつむき・首の傾きが読める
        private static readonly (Vector3 cam, Vector3 look, float h) Side =
            (new Vector3(2.6f, 0.9f, -0.6f), new Vector3(0f, 0.75f, -0.6f), 1.6f);

        private IEnumerator Shot(string name, (Vector3 cam, Vector3 look, float h)? f)
        {
            if (f.HasValue) _rig.FramePreview(f.Value.cam, f.Value.look, f.Value.h);
            // null＝中継視点。寄り枠を一度でも組むとカメラ位置が試合の構図へ戻らないので、
            // 中継視点は TvOnly の回でだけ撮る（寄り枠を一切組まない）

            _rig.RenderNow();   // RT へ同期描画（WaitForEndOfFrame はエディタが裏だと来ないので使わない）
            yield return null;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = _rig.Target;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }
    }
}
#endif

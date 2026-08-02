#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #332（投球の軌道描画＋捕球）の受け入れ撮影。
    /// コースを振った球を、リリース〜捕球まで連番で撮る。静止1枚では追従とタイミングのずれが
    /// 見抜けないため各球5コマ撮る。
    ///
    /// あわせて**捕球の実測**をログへ残す（絵ではカメラの狙い外しと配管ミスが区別できない）:
    ///  ・選ばれた捕球クリップ／ミット中心と狙点のずれ（受け入れ ≦0.12m＝ミット半径）
    ///  ・球がミットに達するより前にミットが捕り終えていないか（早閉じの検出）
    /// </summary>
    public sealed class Duel332Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/i332";
        private const int W = 1600;
        private const int H = 900;

        private DuelStageRig _rig;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[i332] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 8; i++) { _rig.Tick(1f / 60f); yield return null; }

            // engine の場と同じ規約: +x=一塁側=左打者側。右打者（既定）に対して
            //   外角低め = 打者から遠い側 = +x の低め / 内角高め = 打者に近い側 = -x の高め
            // ストライクゾーンは x=±0.216・y=0.50〜1.05（StrikeZone の既定）
            yield return Pitch("10-outside-low", 0.20f, 0.55f, 0.44f, 0.30f, DuelPitchOutcome.CalledStrike);
            yield return Pitch("20-inside-high", -0.20f, 1.00f, 0.42f, 0.45f, DuelPitchOutcome.SwingMiss);
            // 5方向クリップの選択を全部踏む: 真ん中/うんと高め/うんと低め/大きく外れた内角
            yield return Pitch("30-center", 0.00f, 0.78f, 0.45f, 0.30f, DuelPitchOutcome.Take);
            yield return Pitch("31-very-high", 0.00f, 1.15f, 0.43f, 0.35f, DuelPitchOutcome.Take);
            yield return Pitch("32-very-low", 0.00f, 0.35f, 0.47f, 0.25f, DuelPitchOutcome.Take);
            yield return Pitch("33-way-inside", -0.40f, 0.75f, 0.46f, 0.30f, DuelPitchOutcome.Take);
            // 軟投（飛翔0.52s）と速球（0.40s）で早閉じ・遅閉じが出ないこと
            yield return Pitch("34-slow", 0.10f, 0.70f, 0.52f, 0.15f, DuelPitchOutcome.CalledStrike);
            yield return Pitch("35-fast", -0.10f, 0.90f, 0.40f, 0.50f, DuelPitchOutcome.SwingMiss);
            // 死球コース: 右打者の内角を大きく外して打者の体へ（捕手は捕らない分岐）
            yield return Pitch("40-hbp", -0.55f, 0.95f, 0.47f, 0.20f, DuelPitchOutcome.HitByPitch);

            // コースなし（合成列）: ゾーン中央へ運ぶだけ＝コースを主張しない
            yield return PitchNoData("60-nodata");

            Debug.Log("[i332] DONE");
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private IEnumerator Pitch(string tag, float plateX, float plateY, float flight, float ivb,
            DuelPitchOutcome outcome)
        {
            // 実機と同じ流れ: 構え（ここで球が手に入る）→振りかぶりを実時間で進める→発射
            // 狙い=実着弾（issue #396前と同じ・残差ゼロ）＝このハーネスの元の目的（#332 コース＋捕球IK）は不変
            yield return Windup(tag);
            var f = new DuelPitchFlight(plateX, plateY, plateX, plateY, flight, ivb, 0f);
            _rig.OnPitchReleased(-1f, outcome, f);
            Debug.Log("[i332] === " + tag + " plate=(" + plateX.ToString("F2") + "," + plateY.ToString("F2")
                + ") flight=" + flight.ToString("F2") + " ivb=" + ivb.ToString("F2") + " / "
                + _rig.CatchDiagForCapture() + " ===");
            yield return Frames(tag, flight);
        }

        /// <summary>構え→振りかぶりを実時間で進める。球が手に入っている様子も1枚残す（issue #332）。</summary>
        private IEnumerator Windup(string tag)
        {
            _rig.OnPitchWindup();
            float t = 0f;
            while (t < DuelStageRig.WindupSeconds * 0.55f)
            {
                _rig.Tick(1f / 60f); t += 1f / 60f; yield return null;
            }
            yield return Shot(tag + "-hold", Side);
            while (t < DuelStageRig.WindupSeconds)
            {
                _rig.Tick(1f / 60f); t += 1f / 60f; yield return null;
            }
        }

        private IEnumerator PitchNoData(string tag)
        {
            yield return Windup(tag);
            _rig.OnPitchReleased(-1f, DuelPitchOutcome.Take, null);
            Debug.Log("[i332] === " + tag + " (コースなし＝合成列) / " + _rig.CatchDiagForCapture() + " ===");
            yield return Frames(tag, 0.45f);
        }

        /// <summary>
        /// リリース直後〜捕球までを5コマ。各コマで球とミットの実測を残す。
        /// 併せて全フレームを走査して「ミット中心と球の最接近」と「早閉じ」を判定する。
        /// </summary>
        private IEnumerator Frames(string tag, float flight)
        {
            float[] marks = { 0f, flight * 0.3f, flight * 0.6f, flight * 0.9f, flight };
            float t = 0f;
            float closest = 999f, closestAt = -1f;
            for (int m = 0; m < marks.Length; m++)
            {
                while (t < marks[m])
                {
                    _rig.Tick(1f / 60f); t += 1f / 60f;
                    float d = _rig.BallToMittMeters();
                    if (d >= 0f && d < closest) { closest = d; closestAt = t; }
                    yield return null;
                }
                Debug.Log("[i332] " + tag + " t=" + t.ToString("F3") + " " + _rig.BallDiagForCapture());
                yield return Shot(tag + "-t" + m, Side);          // 軌道の垂れ・リリース高が読める
                yield return Shot(tag + "-c" + m, BehindPlate);   // コースの左右・高さが読める
            }
            // 捕球の瞬間とその直後（ミットの吸収と球の追従）
            for (int k = 0; k < 2; k++)
            {
                float until = t + 0.08f;
                while (t < until)
                {
                    _rig.Tick(1f / 60f); t += 1f / 60f;
                    float d = _rig.BallToMittMeters();
                    if (d >= 0f && d < closest) { closest = d; closestAt = t; }
                    yield return null;
                }
                Debug.Log("[i332] " + tag + " 捕球+" + ((k + 1) * 0.08f).ToString("F2") + " "
                    + _rig.BallDiagForCapture() + " / " + _rig.CatchDiagForCapture());
                yield return Shot(tag + "-k" + k, CatchClose);
            }
            while (t < flight + 0.45f)
            {
                _rig.Tick(1f / 60f); t += 1f / 60f;
                float d = _rig.BallToMittMeters();
                if (d >= 0f && d < closest) { closest = d; closestAt = t; }
                yield return null;
            }
            Debug.Log("[i332] " + tag + " ★最接近=" + closest.ToString("F3") + "m at t="
                + closestAt.ToString("F3") + "s（受け入れ ≦0.12m）");
        }

        // 真横（一塁側）から投手と本塁を1枚に収める＝弾道の形（垂れ方）が読める。
        // 縦収め11mなら16:9で横19.6m写るのでマウンド(z=18.44)〜本塁(z=0)が入る
        private static readonly (Vector3 cam, Vector3 look, float h) Side =
            (new Vector3(13f, 2.5f, 9.2f), new Vector3(0f, 1.2f, 9.2f), 11f);
        // 本塁の後方やや一塁側（球審の胴を避ける）＝コースの左右・高さが読める
        private static readonly (Vector3 cam, Vector3 look, float h) BehindPlate =
            (new Vector3(1.6f, 1.5f, -3.4f), new Vector3(0f, 1.0f, 2.0f), 3.0f);
        // 捕球の寄り（一塁側から捕手のミットを横から）＝球がミットの中にあるか前後が分離できる
        private static readonly (Vector3 cam, Vector3 look, float h) CatchClose =
            (new Vector3(2.4f, 0.9f, -0.6f), new Vector3(0f, 0.65f, -0.6f), 1.4f);

        private IEnumerator Shot(string name, (Vector3 cam, Vector3 look, float h) f)
        {
            _rig.FramePreview(f.cam, f.look, f.h);
            yield return null;
            yield return new WaitForEndOfFrame();
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
    }
}
#endif

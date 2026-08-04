#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】バット軌跡のスイング武装ゲート（issue #444）の受け入れ実測。
    /// リグを直駆動して各結果（空振り/ファール/インプレー/見逃し/見送り）を1球ずつ流し、
    /// ・武装中に積んだサンプル数（打ちにいく振り＝従来どおり積む）
    /// ・据え置き入り以降に積んだサンプル数（期待0＝構えへ戻る動きで積まない）
    /// ・旧実装なら積んでいた抑止数（期待>0＝ゲートが実際に効いた証拠）
    /// をコンソールへ出す。絵はスイング中（軌跡あり）と戻り中（軌跡なし）を撮る。使い捨て。
    /// </summary>
    public sealed class BatTrailGateShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/bat-trail-444";
        private const int W = 1280;
        private const int H = 960;

        private DuelStageRig _rig;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[Trail444] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            yield return RunCase(DuelPitchOutcome.SwingMiss, "miss", swing: true);
            yield return RunCase(DuelPitchOutcome.FoulContact, "foul", swing: true);
            yield return RunCase(DuelPitchOutcome.InPlayContact, "inplay", swing: true);
            yield return RunCase(DuelPitchOutcome.CalledStrike, "called", swing: false);
            yield return RunCase(DuelPitchOutcome.Take, "take", swing: false);

            Debug.Log("[Trail444] DONE → " + Dir);
        }

        private IEnumerator RunCase(DuelPitchOutcome outcome, string name, bool swing)
        {
            _rig.ResetForNewPlateAppearance();
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.ResetBatTrailCountersForCapture();

            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, outcome);

            float t = 0f, holdFrom = -1f;
            int sampledAtHold = -1;
            bool shotSwing = false, shotReturn = false;
            while (t < 5f)
            {
                _rig.Tick(1f / 60f); t += 1f / 60f;
                yield return null;

                // スイング中（軌跡が積まれ始めた所）の絵。ファウル/インプレーは振り全体で2サンプル
                // しか積まれない（接触で打ち切りが早い）ので、しきい値は2に置く。
                if (!shotSwing && _rig.BatTrailSampledForCapture >= 2)
                {
                    shotSwing = true;
                    yield return Capture(name + "-1-swing");
                }
                if (holdFrom < 0f)
                {
                    // 据え置き入り＝振り抜きの打ち切り（ここまでが「打ちにいく振り」）。
                    if (_rig.BatterHolding) { holdFrom = t; sampledAtHold = _rig.BatTrailSampledForCapture; }
                    continue;
                }
                // 戻り中（据え置き0.8sが明けて構えへ戻る途中）の絵。
                if (!shotReturn && t - holdFrom >= 1.2f)
                {
                    shotReturn = true;
                    yield return Capture(name + "-2-return");
                }
                if (t - holdFrom > 2.5f) break;
            }

            int sampled = _rig.BatTrailSampledForCapture;
            int suppressed = _rig.BatTrailSuppressedForCapture;
            int afterHold = sampledAtHold >= 0 ? sampled - sampledAtHold : sampled;
            bool ok = swing
                ? sampledAtHold > 0 && afterHold == 0 && suppressed > 0
                : sampled == 0;
            Debug.Log($"[Trail444] {name}: swing中={sampledAtHold} 据え置き以降={afterHold} " +
                      $"抑止={suppressed} 総計={sampled} → {(ok ? "OK" : "NG")}");
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

        private void OnDisable()
        {
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }
    }
}
#endif

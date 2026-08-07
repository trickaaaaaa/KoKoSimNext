#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #514 実機確認: バント構えの立ち向きが直っているかを、
    /// 実際の試合カメラ（DuelStageRig既定のApplyCalibration）で撮る。右打者・左打者の両方、
    /// 移行クリップ途中の飛び確認、通常構え・スイングの回帰確認（#444/#446）を一括で撮る。
    /// </summary>
    public sealed class DuelBuntFacingShooter : MonoBehaviour
    {
        private const string Dir = "out/overnight/shots/issue-514";
        private const int W = 960;
        private const int H = 540;

        private const string SpawnFlag = "KokoSim.DuelBuntFacingShooter.Spawn";
        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~BuntFacingShootAuto").AddComponent<DuelBuntFacingShooter>();
        }

        private DuelStageRig _rig;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[BuntFacing] rig not available"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            foreach (var left in new[] { false, true })   // false=右打者, true=左打者
            {
                string tag = left ? "L" : "R";
                _rig.SetHandedness(left, false);
                for (int i = 0; i < 6; i++) { _rig.Tick(1f / 60f); yield return null; }

                // ① 通常の構え（回帰確認 #444/#446 のベースライン）
                yield return Capture(tag + "-1-normal-idle");

                // ② バント構えへ（Idle_To_Bunt を通す十分なリード）。移行クリップは1.10秒。
                _rig.PrepareBatterBunt(true, 2.0f);
                yield return Advance(0.08f); yield return Capture(tag + "-2a-bunt-in-0.08s");   // クロスフェード中
                yield return Advance(0.35f); yield return Capture(tag + "-2b-bunt-in-0.43s");   // 移行クリップ中盤
                yield return Advance(0.55f); yield return Capture(tag + "-2c-bunt-in-0.98s");   // 移行クリップ終盤
                yield return Advance(0.15f); yield return Capture(tag + "-2d-bunt-loop-1.13s");  // ループへ切替直後
                yield return Advance(0.50f); yield return Capture(tag + "-3-bunt-loop-settled"); // 構え保持

                // ③ バント解除→通常の構えへ戻る（回帰: 元の絵に戻るか）
                _rig.PrepareBatterBunt(false);
                yield return Advance(0.6f);
                yield return Capture(tag + "-4-bunt-off-back-to-normal");

                // ④ 通常スイング（回帰確認 #444/#446。面通過=リリース+0.45s）
                _rig.OnPitchWindup();
                yield return Advance(4f / 60f);
                _rig.OnPitchReleased(0f, DuelPitchOutcome.SwingMiss);
                yield return Advance(0.45f);
                yield return Capture(tag + "-5-swing-contact");
            }

            Debug.Log("[BuntFacing] DONE");
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private IEnumerator Advance(float seconds)
        {
            float t = 0f;
            while (t < seconds) { _rig.Tick(1f / 60f); t += 1f / 60f; yield return null; }
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
            Debug.Log("[BuntFacing] captured " + name);
        }
    }
}
#endif

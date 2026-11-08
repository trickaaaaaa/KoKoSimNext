#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #818 の確認撮影。打席の途中で投手の左右が裏返る継投を
    /// リグ単体で再現する: 1球投げる → 窓が開く頃（リリース後 <see cref="WindowAfterRelease"/> 秒）に
    /// <see cref="DuelStageRig.SetHandedness"/> で投手だけ裏返す → 窓の間を撮り、次の1球まで流す。
    /// 比較として「打席の頭の交代」「交代なしの窓」も同じ時刻で撮る。
    /// 投手の実測（クリップ時刻・基準からのヨー差・位置差・ルートモーション・フェード中か）を毎コマ残す。
    /// </summary>
    public sealed class PitcherSwapShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/pitcher-swap-818";
        private const int W = 960;
        private const int H = 720;
        private const float WindowAfterRelease = 1.2f;
        private const float WindowSeconds = 1.0f;   // 前の球の予約（リリース後≈2.3s）より前に窓を閉じる＝実機の窓の見え方

        private DuelStageRig _rig;
        private StreamWriter _log;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            _log = new StreamWriter(Path.Combine(Dir, "log.txt"));
            _log.WriteLine("case\tphase\tt\tclipT\tdYaw\tdx\tdz\trootMotion\tfading");

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[i818] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);

            yield return Case("A-midpa-R2L", startLeft: false, midPaLeft: true, headSwap: false);
            yield return Case("B-midpa-L2R", startLeft: true, midPaLeft: false, headSwap: false);
            yield return Case("C-nochange", startLeft: false, midPaLeft: false, headSwap: false);
            yield return Case("D-head-R2L", startLeft: false, midPaLeft: true, headSwap: true);

            _log.Close();
            Debug.Log("[i818] DONE → " + Dir);
        }

        private IEnumerator Case(string tag, bool startLeft, bool midPaLeft, bool headSwap)
        {
            _rig.SetHandedness(false, startLeft);
            _rig.ResetForNewPlateAppearance();
            for (int i = 0; i < 20; i++) yield return Step(tag, "settle", i / 60f, false);

            // 1球目（見逃し）
            _rig.OnPitchWindup();
            float t = 0f;
            while (t < DuelStageRig.WindupSeconds) { yield return Step(tag, "windup", t, false); t += 1f / 60f; }
            // 次のリリースまでの間合いは実機と同じ既定値（MatchLiveController が渡す duelPitchIntervalSeconds）
            _rig.OnPitchReleased(DuelStageRig.DefaultPitchInterval, DuelPitchOutcome.Take,
                new DuelPitchFlight(0f, 0.7f, 0f, 0.7f, 0.45f, 0.35f, 0f));
            t = 0f;
            while (t < WindowAfterRelease) { yield return Step(tag, "after-release", t, false); t += 1f / 60f; }
            yield return Shot(tag + "-0-before-swap");

            // 窓が開く。打席の頭なら仕切り直してから当てる（MatchLiveController.OpenWindow と同じ順）
            if (headSwap) _rig.ResetForNewPlateAppearance();
            _rig.SetHandedness(false, midPaLeft);
            float sinceRelease = WindowAfterRelease;
            t = 0f;
            bool shotA = false, shotB = false;
            while (t < WindowSeconds)
            {
                yield return Step(tag, "window", t, false);
                if (!shotA && t >= 0.05f) { shotA = true; yield return Shot(tag + "-1-window-0.05s"); }
                if (!shotB && t >= WindowSeconds - 0.1f) { shotB = true; yield return Shot(tag + "-2-window-end"); }
                t += 1f / 60f;
            }
            sinceRelease += WindowSeconds;

            // 次の1球。MatchLiveController.BeginPitchWindupOrBall と同じ分岐:
            // 打席の頭・セットで止まっている投手は振りかぶりから、それ以外は前の球の予約どおりにリリースを待つ
            bool fromWindup = headSwap || _rig.PitcherAwaitsWindup;
            float untilRelease = fromWindup ? DuelStageRig.WindupSeconds : DuelStageRig.DefaultPitchInterval - sinceRelease;
            Debug.Log("[i818] " + tag + " 次の球=" + (fromWindup ? "振りかぶりから" : "予約どおり") + " リリースまで=" + untilRelease.ToString("F2"));
            if (fromWindup) _rig.OnPitchWindup();
            t = 0f;
            bool shotC = false;
            while (t < untilRelease)
            {
                yield return Step(tag, "next-windup", t, false);
                if (!shotC && t >= untilRelease * 0.6f) { shotC = true; yield return Shot(tag + "-3-next-windup"); }
                t += 1f / 60f;
            }
            _rig.OnPitchReleased(-1f, DuelPitchOutcome.Take, new DuelPitchFlight(0f, 0.7f, 0f, 0.7f, 0.45f, 0.35f, 0f));
            bool shotD = false;
            for (int i = 0; i < 30; i++)
            {
                yield return Step(tag, "next-release", i / 60f, false);
                if (!shotD && i == 6) { shotD = true; yield return Shot(tag + "-4-next-release"); }
            }
        }

        private IEnumerator Step(string tag, string phase, float t, bool _)
        {
            _rig.Tick(1f / 60f);
            _log.WriteLine(tag + "\t" + phase + "\t" + t.ToString("F3") + "\t" + _rig.PitcherDiagForCapture());
            yield return null;
        }

        // 投手を正面やや一塁側から（右打者の後ろ＝本塁側から投手を見る）
        private static readonly (Vector3 cam, Vector3 look, float h) PitcherView =
            (new Vector3(2.0f, 1.6f, 9.0f), new Vector3(0f, 1.1f, 18.0f), 2.6f);

        private IEnumerator Shot(string name)
        {
            _rig.FramePreview(PitcherView.cam, PitcherView.look, PitcherView.h);
            _rig.RenderNow();
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
            _log?.Close();
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }
    }
}
#endif

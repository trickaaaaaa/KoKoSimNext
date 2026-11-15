#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】顔ののっぺらぼう化と打者ヘルメットの被りの深さの確認撮影。
    /// 打者・投手・捕手・走者の顔を顔自身の軸（正面・横・斜め）から寄りで撮り、打者は試合の構図でも撮る。
    /// 出力先と接頭辞は静的フィールドで渡す（撮り比べの前後を同じ場所へ並べるため）。
    /// </summary>
    public sealed class DuelFaceHelmetShooter : MonoBehaviour
    {
        public static string Dir = "screenshots/face-helmet";
        public static string Prefix = "";
        /// <summary>較正の試し打ち用（null なら既定）。</summary>
        public static Vector3? HelmetOffset;
        private const int W = 1200;
        private const int H = 900;

        private DuelStageRig _rig;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[face] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            var cal = DuelCalibration.Default;
            if (HelmetOffset.HasValue) cal.HelmetOffset = HelmetOffset.Value;
            _rig.ApplyCalibration(cal);
            for (int i = 0; i < 8; i++) { _rig.Tick(1f / 60f); yield return null; }

            // 試合の構図（寄り枠なし）
            _rig.ClearPreviewFrame();
            _rig.ApplyCalibration(cal);
            yield return Shoot("match");

            yield return Face(0, "batter");
            var clip = "batter:\n" + _rig.HelmetClipDiagForCapture(0);
            yield return Face(1, "pitcher");
            yield return Face(2, "catcher");
            Vector3 c0, f0, r0;
            if (_rig.RunnerHelmetFrameForCapture(new Vector3(6f, 0f, 6f), out c0, out f0, out r0))
            {
                for (int i = 0; i < 6; i++) { _rig.Tick(1f / 60f); yield return null; }
                _rig.RunnerHelmetFrameForCapture(new Vector3(6f, 0f, 6f), out c0, out f0, out r0);
                yield return Face(3, "runner");
                clip += "\nrunner:\n" + _rig.HelmetClipDiagForCapture(3);
            }
            // 振り（空振り・インプレー）の間もめり込みを測る。振り抜きで肩が顎の下へ上がる＝深く被せると
            // 耳フラップの下端が肩・襟へ当たりうる。据え置き（振り切り）の絵も撮る
            foreach (var outcome in new[] { DuelPitchOutcome.SwingMiss, DuelPitchOutcome.InPlayContact })
            {
                _rig.ClearPreviewFrame();
                _rig.ApplyCalibration(cal);
                _rig.ResetForNewPlateAppearance();
                for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }
                _rig.OnPitchWindup();
                for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
                _rig.OnPitchReleased(0f, outcome);
                clip += "\nswing " + outcome + ":\n";
                bool shot = false;
                for (int f = 0; f < 240; f++)
                {
                    _rig.Tick(1f / 60f);
                    yield return null;
                    if (f % 5 == 0)
                    {
                        var d = _rig.HelmetClipDiagForCapture(0);
                        if (d.Contains("Suit")) clip += "t=" + (f / 60f).ToString("F2") + " " + d;
                    }
                    if (!shot && _rig.BatterHolding)
                    {
                        shot = true;
                        clip += "hold t=" + (f / 60f).ToString("F2") + " " + _rig.HelmetClipDiagForCapture(0);
                        Vector3 c, fw, up;
                        if (_rig.HeadFrameForCapture(0, out c, out fw, out up))
                        {
                            var right = Vector3.Cross(up, fw).normalized;
                            _rig.FramePreview(c + (fw + right).normalized * 1.6f, c - up * 0.1f, 0.6f);
                            yield return Shoot("swing-" + (outcome == DuelPitchOutcome.SwingMiss ? "miss" : "inplay") + "-hold");
                        }
                    }
                }
            }
            File.WriteAllText(Path.Combine(Dir, Prefix + "clip.txt"), clip);
            Debug.Log("[face] DONE");
        }

        private void OnDisable() { if (_rig != null) { _rig.Dispose(); _rig = null; } }

        private IEnumerator Face(int who, string tag)
        {
            Vector3 c, f, up;
            if (!_rig.HeadFrameForCapture(who, out c, out f, out up)) yield break;
            var right = Vector3.Cross(up, f).normalized;
            const float d = 1.4f;
            const float h = 0.42f;
            _rig.FramePreview(c + f * d, c, h);
            yield return Shoot(tag + "-front");
            _rig.FramePreview(c + right * d, c, h);
            yield return Shoot(tag + "-side");
            _rig.FramePreview(c + (f + right * 0.8f).normalized * d + up * 0.15f, c, h);
            yield return Shoot(tag + "-diag");
        }

        private IEnumerator Shoot(string name)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            _rig.RenderNow();
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = _rig.Target;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, Prefix + name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
        }
    }
}
#endif

#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】残差の体寄せの下方向クランプ（issue #440）の受け入れ実測。
    /// リグを直駆動して低め外れ・高め外れ・横外れの3球（見送り＝捕手が捕る分岐）を流し、
    /// ・捕手ルートの BasePos からのオフセット最小y（期待: 低めでも 0 以上＝地面へ沈まない）
    /// ・上・左右の体寄せの最大量（期待: 従来どおり残差ぶん寄る）
    /// ・捕球の瞬間の CatchDiag（ミットが実着弾点を掴めているか。受け入れ ずれ≦0.12m）
    /// をコンソールへ出す。絵は低め外れの捕球の瞬間を撮る。使い捨て。
    /// </summary>
    public sealed class CatcherClampShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/catcher-clamp-440";
        private const int W = 1280;
        private const int H = 960;

        private DuelStageRig _rig;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[Clamp440] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            // 狙い(0, 0.55) からの外れ。残差＝実着弾−狙い。飛翔0.45sは140km/h級の実測相当。
            yield return RunCase("low", new DuelPitchFlight(0f, 0.15f, 0f, 0.55f, 0.45f, 0.4f, 0f), shoot: true);
            yield return RunCase("high", new DuelPitchFlight(0f, 0.95f, 0f, 0.55f, 0.45f, 0.4f, 0f), shoot: false);
            yield return RunCase("side", new DuelPitchFlight(0.45f, 0.55f, 0f, 0.55f, 0.45f, 0.4f, 0f), shoot: false);

            Debug.Log("[Clamp440] DONE → " + Dir);
        }

        private IEnumerator RunCase(string name, DuelPitchFlight pitch, bool shoot)
        {
            _rig.ResetForNewPlateAppearance();
            for (int i = 0; i < 12; i++) { _rig.Tick(1f / 60f); yield return null; }

            _rig.OnPitchWindup();
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchReleased(0f, DuelPitchOutcome.Take, pitch);

            float minY = float.MaxValue, maxY = float.MinValue, maxAbsX = 0f;
            // ミットと狙点のずれは「捕球の瞬間（IK重み=1）」で最小になる＝1サンプルの決め打ちでなく
            // 窓内の最小値で受け入れ判定する（w=0.70 の戻り際を測って NG と誤読しない）。
            float minDev = float.MaxValue;
            bool shotDone = false;
            float t = 0f;
            while (t < pitch.FlightSeconds + 1.2f)
            {
                _rig.Tick(1f / 60f); t += 1f / 60f;
                yield return null;
                var off = _rig.CatcherRootOffsetForCapture();
                if (off.y < minY) minY = off.y;
                if (off.y > maxY) maxY = off.y;
                if (Mathf.Abs(off.x) > maxAbsX) maxAbsX = Mathf.Abs(off.x);
                var m = System.Text.RegularExpressions.Regex.Match(
                    _rig.CatchDiagForCapture(), @"ずれ=([0-9.]+)m");
                if (m.Success)
                {
                    float dev = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (dev < minDev) minDev = dev;
                }
                // 捕球の瞬間の絵。
                if (shoot && !shotDone && t >= pitch.FlightSeconds)
                {
                    shotDone = true;
                    yield return Capture(name + "-catch");
                }
            }

            Debug.Log($"[Clamp440] {name}: 体寄せ最小y={minY:F3} 最大y={maxY:F3} 最大|x|={maxAbsX:F3} " +
                      $"最小ずれ={(minDev == float.MaxValue ? -1f : minDev):F3}m");
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

#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】残差の体寄せの受け入れ実測。使い捨て。
    /// リグを直駆動して低め外れ・高め外れ・横外れの3球（見送り＝捕手が捕る分岐）を流す。
    /// ・高さクランプ（issue #440/#466）: ルートオフセットの最小y ≧ 0（地面へ沈まない）かつ
    ///   最大y ≈ 0（宙に浮かない）。左右の体寄せ（横っ跳び）は従来どおり・
    ///   捕球の瞬間のミットずれ ≦ 0.12m（高さはクリップ選択＋ミットIK＋胴アシストで回収）
    /// ・捕球後の戻り（監督報告 2026-08-04「定位置へ瞬間移動＝全身フレーミング」）:
    ///   球がミットの中にある間はルートが動かない（ミット中最大step≈0）・
    ///   球が消えてから1コマ最大stepが小さく（旧: 0.2s戻り＝大外れで0.19m/コマ級）・
    ///   最終的に基準位置へ戻り切る（最終|off|≈0）
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
            yield return RunCase("low", new DuelPitchFlight(0f, 0.15f, 0f, 0.55f, 0.45f, 0.4f, 0f), shoot: false);
            // 高めは絵も撮る（#466 の受け入れ「足は地面・胴の伸び上がりでミットを上げる」を目視確認）
            yield return RunCase("high", new DuelPitchFlight(0f, 0.95f, 0f, 0.55f, 0.45f, 0.4f, 0f), shoot: true);
            yield return RunCase("side", new DuelPitchFlight(0.45f, 0.55f, 0f, 0.55f, 0.45f, 0.4f, 0f), shoot: true);

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
            // 瞬間移動の検出: 1描画フレームあたりのルート移動量[m]。ポーズは12fpsなので動く時は
            // 1ポーズコマぶんがまとめて出る＝最大stepが「1コマで飛ぶ距離」。
            const float inMitt = 0.45f;   // DuelStageRig.BallInMittSeconds（private のため写し）
            float maxStepInMitt = 0f, maxStepAfter = 0f, offAtHide = -1f;
            var prevOff = _rig.CatcherRootOffsetForCapture();
            bool shotCatch = false, shotHold = false, shotReturn = false;
            float t = 0f;
            while (t < pitch.FlightSeconds + inMitt + 1.2f)
            {
                _rig.Tick(1f / 60f); t += 1f / 60f;
                yield return null;
                var off = _rig.CatcherRootOffsetForCapture();
                float rootStep = (off - prevOff).magnitude;
                prevOff = off;
                if (t >= pitch.FlightSeconds && t < pitch.FlightSeconds + inMitt)
                {
                    if (rootStep > maxStepInMitt) maxStepInMitt = rootStep;
                }
                else if (t >= pitch.FlightSeconds + inMitt)
                {
                    if (offAtHide < 0f) offAtHide = off.magnitude;
                    if (rootStep > maxStepAfter) maxStepAfter = rootStep;
                }
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
                // 絵: 捕球の瞬間 / 球が消える直前（体はまだ寄ったまま＝据え置きの証拠）/ 戻りの途中。
                if (shoot && !shotCatch && t >= pitch.FlightSeconds)
                {
                    shotCatch = true;
                    yield return Capture(name + "-catch");
                }
                if (shoot && !shotHold && t >= pitch.FlightSeconds + inMitt - 0.05f)
                {
                    shotHold = true;
                    yield return Capture(name + "-hold");
                }
                if (shoot && !shotReturn && t >= pitch.FlightSeconds + inMitt + 0.22f)
                {
                    shotReturn = true;
                    yield return Capture(name + "-return");
                }
            }

            var fin = _rig.CatcherRootOffsetForCapture();
            Debug.Log($"[Clamp440] {name}: 体寄せ最小y={minY:F3} 最大y={maxY:F3} 最大|x|={maxAbsX:F3} " +
                      $"最小ずれ={(minDev == float.MaxValue ? -1f : minDev):F3}m " +
                      $"ミット中最大step={maxStepInMitt:F3} 戻り最大step={maxStepAfter:F3} " +
                      $"球消滅時|off|={offAtHide:F3} 最終|off|={fin.magnitude:F3}");
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

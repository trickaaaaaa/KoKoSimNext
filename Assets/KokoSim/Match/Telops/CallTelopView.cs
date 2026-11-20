using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match.Telops
{
    /// <summary>
    /// 判定テロップ（Calls、11種・issue #1179 §2）の画像面を動かす。流さずその場でパッと出す＝
    /// 拡大率1.45→1・不透明度0→1（OutBack）で入り、止めて見せ、拡大率1→1.04・不透明度1→0（InQuad）で消える。
    ///
    /// <para>寿命（合計・入り/出の秒数）は呼び出し側（1球の判定=pitchCallHoldSeconds、打球の判定=
    /// JudgementHoldSeconds）が持つ既存の時計をそのまま渡すだけ＝寿命の正典を二重化しない。
    /// どの面（tl-call-xxx）を出すかは <see cref="SetFace"/> で切り替える（ShowPitchCall/
    /// Match2DPlaybackElement.UpdateJudgementが1プレー1枚の規則を守って呼ぶ）。</para>
    /// </summary>
    public sealed class CallTelopView
    {
        private const double InScale = 1.45;
        private const double OutScale = 1.04;

        private readonly VisualElement _image;
        private string _activeClass;

        public CallTelopView(VisualElement image) => _image = image;

        /// <summary>この回で出す面（tl-call-xxx）に切り替える（前回のクラスは外す）。</summary>
        public void SetFace(string className)
        {
            if (_activeClass == className) return;
            if (_activeClass != null) _image.RemoveFromClassList(_activeClass);
            _image.AddToClassList(className);
            _activeClass = className;
        }

        /// <summary>
        /// 毎フレーム呼ぶ。elapsed/totalSeconds/inSeconds/outSecondsは呼び出し側の時計そのまま
        /// （判定の表＝1球0.85s(in0.12/out0.12) ／ 打球0.60s(in0.10/out0.10)）。
        /// </summary>
        public void Tick(double elapsed, double totalSeconds, double inSeconds, double outSeconds)
        {
            double scale, opacity;
            var outStart = System.Math.Max(inSeconds, totalSeconds - outSeconds);
            if (elapsed < inSeconds)
            {
                var eased = TelopEasing.OutBack(elapsed / System.Math.Max(1e-6, inSeconds));
                scale = InScale + (1.0 - InScale) * eased;
                opacity = System.Math.Clamp(eased, 0.0, 1.0);
            }
            else if (elapsed < outStart)
            {
                scale = 1.0;
                opacity = 1.0;
            }
            else
            {
                var dur = System.Math.Max(1e-6, totalSeconds - outStart);
                var eased = TelopEasing.InQuad((elapsed - outStart) / dur);
                scale = 1.0 + (OutScale - 1.0) * eased;
                opacity = System.Math.Clamp(1.0 - eased, 0.0, 1.0);
            }
            _image.style.scale = new Scale(new Vector2((float)scale, (float)scale));
            _image.style.opacity = (float)opacity;
        }

        public void Hide() => _image.style.opacity = 0f;
    }
}

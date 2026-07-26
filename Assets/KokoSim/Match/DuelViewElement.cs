using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 対決ビューのRT表示要素（design-18 §7-1 A案）。RT 1ピクセル＝物理ピクセル整数倍になるよう
    /// 表示サイズをスナップし、余りは親（.mlive-duel＝盤面と同じ額縁色）のレターボックスに任せる。
    /// 滲みの主因は非整数 scaledPixelsPerPoint（panel-scale-crispness知見）なので、
    /// ポイントでなく物理ピクセル基準で倍率を決める。
    /// </summary>
    public sealed class DuelViewElement : Image
    {
        public DuelViewElement()
        {
            scaleMode = ScaleMode.StretchToFill;   // サイズはこちらで整数倍に固定するため引き伸ばしで良い
            RegisterCallback<AttachToPanelEvent>(_ => parent?.RegisterCallback<GeometryChangedEvent>(OnParentGeometry));
            RegisterCallback<DetachFromPanelEvent>(_ => parent?.UnregisterCallback<GeometryChangedEvent>(OnParentGeometry));
        }

        public void Bind(Texture texture)
        {
            image = texture;
            Snap();
        }

        private void OnParentGeometry(GeometryChangedEvent _) => Snap();

        private void Snap()
        {
            if (parent == null || panel == null) return;
            float sppp = panel.scaledPixelsPerPoint;
            if (sppp <= 0f) return;
            float wPx = parent.resolvedStyle.width * sppp;
            float hPx = parent.resolvedStyle.height * sppp;
            if (float.IsNaN(wPx) || wPx <= 0f || float.IsNaN(hPx) || hPx <= 0f) return;

            int scale = Mathf.Max(1, Mathf.FloorToInt(
                Mathf.Min(wPx / DuelStageRig.RtWidth, hPx / DuelStageRig.RtHeight)));
            style.width = DuelStageRig.RtWidth * scale / sppp;
            style.height = DuelStageRig.RtHeight * scale / sppp;
        }
    }
}

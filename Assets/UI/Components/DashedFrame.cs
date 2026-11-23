using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 点線の角丸枠（部品辞書）。USS の border は点線を描けないので、Painter2D で辺を短い線に刻み、
    /// 角は実線の弧でつなぐ。親いっぱいに絶対配置で重ね、当たり判定は持たない（押せるのは親）。
    /// 線の色・太さ・角丸・刻みはプロパティで渡す（ホームの個別指導の空き枠：#CFC8BB・1.5px・角丸10）。
    /// </summary>
    public sealed class DashedFrame : VisualElement
    {
        public Color LineColor { get; set; } = new Color32(0xCF, 0xC8, 0xBB, 0xFF);
        public float LineWidth { get; set; } = 1.5f;
        public float Radius { get; set; } = 10f;
        public float Dash { get; set; } = 5f;
        public float Gap { get; set; } = 4f;

        public DashedFrame()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;
            generateVisualContent += OnGenerate;
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width < 4f || r.height < 4f) return;
            var p = mgc.painter2D;
            p.strokeColor = LineColor;
            p.lineWidth = LineWidth;
            p.lineCap = LineCap.Butt;

            var h = LineWidth * 0.5f;
            var x0 = r.xMin + h; var y0 = r.yMin + h;
            var x1 = r.xMax - h; var y1 = r.yMax - h;
            var rad = Mathf.Min(Radius, (x1 - x0) * 0.5f, (y1 - y0) * 0.5f);

            // 角（実線の弧）。
            Corner(p, new Vector2(x0 + rad, y0 + rad), rad, 180f, 270f);
            Corner(p, new Vector2(x1 - rad, y0 + rad), rad, 270f, 360f);
            Corner(p, new Vector2(x1 - rad, y1 - rad), rad, 0f, 90f);
            Corner(p, new Vector2(x0 + rad, y1 - rad), rad, 90f, 180f);

            // 辺（点線）。
            DashLine(p, new Vector2(x0 + rad, y0), new Vector2(x1 - rad, y0));
            DashLine(p, new Vector2(x1, y0 + rad), new Vector2(x1, y1 - rad));
            DashLine(p, new Vector2(x1 - rad, y1), new Vector2(x0 + rad, y1));
            DashLine(p, new Vector2(x0, y1 - rad), new Vector2(x0, y0 + rad));
        }

        private static void Corner(Painter2D p, Vector2 c, float radius, float from, float to)
        {
            if (radius <= 0f) return;
            p.BeginPath();
            p.Arc(c, radius, from, to);
            p.Stroke();
        }

        private void DashLine(Painter2D p, Vector2 a, Vector2 b)
        {
            var len = Vector2.Distance(a, b);
            if (len <= 0f) return;
            var dir = (b - a) / len;
            // 両端が線で終わるよう、刻みの数から間隔を均す。
            var period = Dash + Gap;
            var n = Mathf.Max(1, Mathf.RoundToInt((len + Gap) / period));
            var gap = n > 1 ? (len - n * Dash) / (n - 1) : 0f;
            var dash = n > 1 ? Dash : len;
            for (var i = 0; i < n; i++)
            {
                var s0 = i * (dash + gap);
                p.BeginPath();
                p.MoveTo(a + dir * s0);
                p.LineTo(a + dir * Mathf.Min(len, s0 + dash));
                p.Stroke();
            }
        }
    }
}

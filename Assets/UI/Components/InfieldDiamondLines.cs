using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 内野のダイヤモンドの線画（部品辞書）。大きなひし形・小さなひし形・中央の円を、要素いっぱい（900四方を基準）に
    /// Painter2D で描く。つづきからの画面の地にうっすら敷く飾り（当たり判定は持たない）。位置と大きさは使う側の USS。
    /// </summary>
    public sealed class InfieldDiamondLines : VisualElement
    {
        private const float Box = 900f;

        /// <summary>線の色（紙色 #f6f1e7。モックの不透明度4%と同じ見え方＝UI はリニア空間で混ぜるので 0.75%）。</summary>
        public Color LineColor { get; set; } = new Color(0xF6 / 255f, 0xF1 / 255f, 0xE7 / 255f, 0.0075f);

        /// <summary>線の太さ（基準の 900 四方での値）。</summary>
        public float LineWidth { get; set; } = 2f;

        public InfieldDiamondLines()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerate;
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width <= 0f || r.height <= 0f) return;
            var s = Mathf.Min(r.width, r.height) / Box;
            Vector2 P(float x, float y) => new Vector2(r.x + x * s, r.y + y * s);

            var p = mgc.painter2D;
            p.strokeColor = LineColor;
            p.lineWidth = LineWidth * s;
            p.lineJoin = LineJoin.Miter;

            Diamond(p, P(450, 90), P(810, 450), P(450, 810), P(90, 450));
            Diamond(p, P(450, 250), P(650, 450), P(450, 650), P(250, 450));
            p.BeginPath();
            p.Arc(P(450, 450), 46f * s, 0f, 360f);
            p.Stroke();
        }

        private static void Diamond(Painter2D p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            p.BeginPath();
            p.MoveTo(a); p.LineTo(b); p.LineTo(c); p.LineTo(d);
            p.ClosePath();
            p.Stroke();
        }
    }
}

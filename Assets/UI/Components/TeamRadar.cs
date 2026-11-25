using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 1校ぶんの戦力レーダー（試合開始前の画面・2校を重ねず横に並べる）。
    /// 目盛りの六角形（25/50/75%＋外周）・中心からの線・チームの形を 1 つの要素の Painter2D で描く。
    /// 項目名・校名・中心のランクは呼び出し側が上に絶対配置で置く（<see cref="AxisPoint"/> で位置を合わせる）。
    /// 描く領域は要素の contentRect、中心は領域の中心、外周の半径は <see cref="Radius"/>（既定 98px）。
    /// </summary>
    public sealed class TeamRadar : VisualElement
    {
        private static readonly Color GridInner = new Color32(0xEC, 0xE8, 0xDF, 0xFF);
        private static readonly Color GridOuter = new Color32(0xD9, 0xD4, 0xC8, 0xFF);

        private IReadOnlyList<float> _values01 = new float[0];
        private Color _color = Color.black;
        private float _fillAlpha = 0.18f;

        /// <summary>外周（100%）の半径[px]。</summary>
        public float Radius { get; set; } = 98f;

        public TeamRadar()
        {
            AddToClassList("team-radar");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerate;
        }

        /// <summary>値（0..1・真上から時計回り）とチームの色・塗りの不透明度を入れて描き直す。</summary>
        public void SetData(IReadOnlyList<float> values01, Color color, float fillAlpha)
        {
            _values01 = values01 ?? new float[0];
            _color = color;
            _fillAlpha = fillAlpha;
            MarkDirtyRepaint();
        }

        /// <summary>
        /// 中心 <paramref name="center"/> から半径 <paramref name="radius"/> の位置にある i 番目（全 n 本）の軸の点。
        /// 0 番が真上・時計回り（画面座標は y が下向き）。
        /// </summary>
        public static Vector2 AxisPoint(Vector2 center, float radius, int i, int n)
        {
            var a = -Mathf.PI / 2f + 2f * Mathf.PI * i / n;
            return center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var n = _values01.Count;
            if (n < 3) return;
            var r = contentRect;
            if (r.width < 4f || r.height < 4f) return;
            var c = new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f);
            var p = mgc.painter2D;
            p.lineJoin = LineJoin.Round;
            p.lineCap = LineCap.Round;

            // 目盛り（25/50/75% は淡く、外周だけ一段濃く）と中心からの線。どれも 1px。
            p.lineWidth = 1f;
            foreach (var f in new[] { 0.25f, 0.5f, 0.75f, 1f })
            {
                p.strokeColor = f >= 1f ? GridOuter : GridInner;
                Polygon(p, c, n, i => Radius * f);
                p.Stroke();
            }
            p.strokeColor = GridInner;
            for (var i = 0; i < n; i++)
            {
                p.BeginPath();
                p.MoveTo(c);
                p.LineTo(AxisPoint(c, Radius, i, n));
                p.Stroke();
            }

            // チームの形：塗り（薄く）→ 輪郭（2.5px・角は丸める）。
            p.fillColor = new Color(_color.r, _color.g, _color.b, _fillAlpha);
            Polygon(p, c, n, i => Radius * Mathf.Clamp01(_values01[i]));
            p.Fill();
            p.strokeColor = _color;
            p.lineWidth = 2.5f;
            Polygon(p, c, n, i => Radius * Mathf.Clamp01(_values01[i]));
            p.Stroke();
        }

        private static void Polygon(Painter2D p, Vector2 c, int n, System.Func<int, float> radiusAt)
        {
            p.BeginPath();
            p.MoveTo(AxisPoint(c, radiusAt(0), 0, n));
            for (var i = 1; i < n; i++) p.LineTo(AxisPoint(c, radiusAt(i), i, n));
            p.ClosePath();
        }
    }
}

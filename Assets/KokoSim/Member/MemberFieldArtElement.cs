using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Member
{
    /// <summary>
    /// メンバー設定の球場の絵（背番号1〜9のカードの下地）。556×700 の設計座標で描き、要素の大きさに合わせて
    /// 等倍率で縮める（横は中央・縦は下端そろえ）。カードの置き場所も同じ変換（<see cref="ToLocal"/>）で決める。
    /// 試合の2D俯瞰などが使う <see cref="BaseballFieldElement"/>（メートル座標）とは別物＝この画面の絵だけを持つ。
    /// 色は scorebook.uss のトークン（--sbk-field-*）から読む。
    /// </summary>
    public sealed class MemberFieldArtElement : VisualElement
    {
        public const float DesignW = 556f;
        public const float DesignH = 700f;

        private static readonly Vector2 Home = new Vector2(278f, 676f);
        private static readonly Vector2 LeftEnd = new Vector2(-60f, 338f);    // 左のファウルラインの先（本塁から45°）
        private static readonly Vector2 RightEnd = new Vector2(616f, 338f);
        private const float OutfieldRadius = 478f;
        private const float MoundRadius = 20f;
        private const float BaseHalfDiagonal = 8.5f;   // 12pxの正方形を45°回したときの中心→頂点

        private static readonly Vector2[] Bases =
        {
            new Vector2(384f, 504f), new Vector2(278f, 398f), new Vector2(172f, 504f),
        };

        private static readonly CustomStyleProperty<Color> GrassProp = new CustomStyleProperty<Color>("--sbk-field-grass");
        private static readonly CustomStyleProperty<Color> GrassEdgeProp = new CustomStyleProperty<Color>("--sbk-field-grass-edge");
        private static readonly CustomStyleProperty<Color> DirtProp = new CustomStyleProperty<Color>("--sbk-field-dirt");
        private static readonly CustomStyleProperty<Color> LineProp = new CustomStyleProperty<Color>("--sbk-field-line");
        private static readonly CustomStyleProperty<Color> BaseProp = new CustomStyleProperty<Color>("--sbk-field-base");

        private Color _grass = new Color32(0x4B, 0x7A, 0x44, 0xFF);
        private Color _grassEdge = new Color32(0x5A, 0x8A, 0x52, 0xFF);
        private Color _dirt = new Color32(0xB4, 0x8A, 0x60, 0xFF);
        private Color _line = new Color32(0xF3, 0xEF, 0xE6, 0xCC);
        private Color _base = new Color32(0xF6, 0xF3, 0xEC, 0xFF);

        public MemberFieldArtElement()
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("ms-field__art");
            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyles);
        }

        private void OnStyles(CustomStyleResolvedEvent e)
        {
            if (e.customStyle.TryGetValue(GrassProp, out var g)) _grass = g;
            if (e.customStyle.TryGetValue(GrassEdgeProp, out var ge)) _grassEdge = ge;
            if (e.customStyle.TryGetValue(DirtProp, out var d)) _dirt = d;
            if (e.customStyle.TryGetValue(LineProp, out var l)) _line = l;
            if (e.customStyle.TryGetValue(BaseProp, out var b)) _base = b;
            MarkDirtyRepaint();
        }

        /// <summary>設計座標→要素内の座標の倍率。レイアウト未確定なら 0。</summary>
        public float Scale
        {
            get
            {
                var w = contentRect.width;
                var h = contentRect.height;
                if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f) return 0f;
                return Mathf.Min(w / DesignW, h / DesignH);
            }
        }

        /// <summary>設計座標（556×700）→ 要素内の座標[px]。</summary>
        public Vector2 ToLocal(Vector2 design)
        {
            var s = Scale;
            var ox = (contentRect.width - DesignW * s) * 0.5f;
            var oy = contentRect.height - DesignH * s;
            return new Vector2(ox + design.x * s, oy + design.y * s);
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var s = Scale;
            if (s <= 0f) return;
            var p = mgc.painter2D;
            var home = ToLocal(Home);

            // 外野の芝（本塁を中心にした扇形）と縁の線
            p.fillColor = _grass;
            FanPath(p, home, s);
            p.Fill();
            p.strokeColor = _grassEdge;
            p.lineWidth = 2f * s;
            FanPath(p, home, s);
            p.Stroke();

            // 内野の土
            p.fillColor = _dirt;
            p.BeginPath();
            p.MoveTo(home);
            p.LineTo(ToLocal(new Vector2(452f, 502f)));
            p.QuadraticCurveTo(ToLocal(new Vector2(278f, 300f)), ToLocal(new Vector2(104f, 502f)));
            p.ClosePath();
            p.Fill();

            // 内野の芝（ひし形）
            p.fillColor = _grass;
            p.BeginPath();
            p.MoveTo(ToLocal(new Vector2(278f, 610f)));
            p.LineTo(ToLocal(new Vector2(384f, 504f)));
            p.LineTo(ToLocal(new Vector2(278f, 398f)));
            p.LineTo(ToLocal(new Vector2(172f, 504f)));
            p.ClosePath();
            p.Fill();

            // マウンド
            p.fillColor = _dirt;
            p.BeginPath();
            p.Arc(ToLocal(new Vector2(278f, 504f)), MoundRadius * s, Angle.Degrees(0f), Angle.Degrees(360f));
            p.ClosePath();
            p.Fill();

            // ファウルライン
            p.strokeColor = _line;
            p.lineWidth = 2f * s;
            p.BeginPath();
            p.MoveTo(home);
            p.LineTo(ToLocal(LeftEnd));
            p.Stroke();
            p.BeginPath();
            p.MoveTo(home);
            p.LineTo(ToLocal(RightEnd));
            p.Stroke();

            // 塁（45°回した正方形）と本塁（五角形）
            p.fillColor = _base;
            var d = BaseHalfDiagonal * s;
            foreach (var b in Bases)
            {
                var c = ToLocal(b);
                p.BeginPath();
                p.MoveTo(new Vector2(c.x, c.y - d));
                p.LineTo(new Vector2(c.x + d, c.y));
                p.LineTo(new Vector2(c.x, c.y + d));
                p.LineTo(new Vector2(c.x - d, c.y));
                p.ClosePath();
                p.Fill();
            }
            p.BeginPath();
            p.MoveTo(ToLocal(new Vector2(271f, 668f)));
            p.LineTo(ToLocal(new Vector2(285f, 668f)));
            p.LineTo(ToLocal(new Vector2(285f, 676f)));
            p.LineTo(ToLocal(new Vector2(278f, 682f)));
            p.LineTo(ToLocal(new Vector2(271f, 676f)));
            p.ClosePath();
            p.Fill();
        }

        // 本塁→左の端→弧→右の端→本塁。画面座標は y が下向きなので、225°→315° が上を通る弧になる。
        private void FanPath(Painter2D p, Vector2 home, float s)
        {
            p.BeginPath();
            p.MoveTo(home);
            p.LineTo(ToLocal(LeftEnd));
            p.Arc(home, OutfieldRadius * s, Angle.Degrees(225f), Angle.Degrees(315f));
            p.ClosePath();
        }
    }
}

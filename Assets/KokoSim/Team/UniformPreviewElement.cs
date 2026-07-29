using KokoSim.Engine.Nation;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Squad
{
    /// <summary>
    /// ユニフォーム配色の 2D 模式図（部品辞書・設計書18 §3.5.1）。
    /// 正面向きの選手シルエットを領域ごとに塗り分け、縦縞も出す。
    ///
    /// <b>3Dの実物プレビューではない</b>（2026-07-29 監督決定）。対決ビューでは帽子が10画素・
    /// ソックスが数画素しか映らず「どこを変えたか」が見えないため、確認用には模式図の方が向く。
    /// 代わりにピクセル化後の見え方とは違う＝実物は対決ビューで見る、と割り切る。
    ///
    /// 形は正規化座標(0..1)の矩形・多角形で定義し、要素サイズへ写す唯一の変換 <see cref="N"/> を通す
    /// （BaseballFieldElement と同じ流儀＝座標系ドリブン）。
    /// </summary>
    public sealed class UniformPreviewElement : VisualElement
    {
        private TeamColors _colors;

        /// <summary>肌・シューズは配色対象外の固定色（DuelStageRig の UniformPalette と揃える）。</summary>
        private static readonly Color Skin = new(0.85f, 0.68f, 0.55f);
        private static readonly Color Shoes = new(0.15f, 0.15f, 0.16f);
        private static readonly Color Ink = new(0.075f, 0.082f, 0.071f);

        public UniformPreviewElement()
        {
            generateVisualContent += Draw;
        }

        public void SetColors(TeamColors colors)
        {
            _colors = colors;
            MarkDirtyRepaint();
        }

        private static Color C(RgbColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        // 正規化座標(0..1) → 要素ローカル座標。図はアスペクト 0.62:1 の枠に収め、余白は中央寄せ。
        private Rect _box;

        private Vector2 N(float x, float y) => new(_box.x + x * _box.width, _box.y + y * _box.height);

        private void Draw(MeshGenerationContext ctx)
        {
            if (_colors == null) return;
            var r = contentRect;
            if (r.width <= 1f || r.height <= 1f) return;

            // 図の縦横比を保って中央に置く（横に伸ばして人が太る事故を防ぐ）
            const float aspect = 0.62f;
            float h = Mathf.Min(r.height, r.width / aspect);
            float w = h * aspect;
            _box = new Rect(r.x + (r.width - w) * 0.5f, r.y + (r.height - h) * 0.5f, w, h);

            var p = ctx.painter2D;
            var shirt = C(_colors.Shirt);
            var pants = C(_colors.Pants);
            var under = C(_colors.Undershirt);
            var socks = C(_colors.Socks);
            var cap = C(_colors.CapCrown);

            // ── 脚（ズボン → ソックス → シューズ）。先に描いて胴で上を隠す ──
            Quad(p, pants, 0.34f, 0.52f, 0.47f, 0.78f);   // 左脚
            Quad(p, pants, 0.53f, 0.52f, 0.66f, 0.78f);   // 右脚
            if (_colors.Stripe.On)
            {
                Stripes(p, 0.34f, 0.52f, 0.47f, 0.78f);
                Stripes(p, 0.53f, 0.52f, 0.66f, 0.78f);
            }
            Quad(p, socks, 0.355f, 0.78f, 0.465f, 0.90f);
            Quad(p, socks, 0.535f, 0.78f, 0.645f, 0.90f);
            Quad(p, Shoes, 0.335f, 0.90f, 0.475f, 0.945f);
            Quad(p, Shoes, 0.525f, 0.90f, 0.665f, 0.945f);

            // ── 腕（袖＝ユニ上／前腕＝アンダーシャツ） ──
            Quad(p, shirt, 0.20f, 0.28f, 0.32f, 0.44f);
            Quad(p, shirt, 0.68f, 0.28f, 0.80f, 0.44f);
            Quad(p, under, 0.215f, 0.44f, 0.305f, 0.60f);
            Quad(p, under, 0.695f, 0.44f, 0.785f, 0.60f);
            Quad(p, Skin, 0.215f, 0.60f, 0.305f, 0.655f);
            Quad(p, Skin, 0.695f, 0.60f, 0.785f, 0.655f);

            // ── 胴（ユニ上＋縦縞）。襟元にアンダーシャツを覗かせる ──
            Quad(p, under, 0.40f, 0.235f, 0.60f, 0.30f);
            Quad(p, shirt, 0.32f, 0.27f, 0.68f, 0.535f);
            if (_colors.Stripe.On) Stripes(p, 0.32f, 0.27f, 0.68f, 0.535f);
            Quad(p, Ink, 0.34f, 0.505f, 0.66f, 0.525f);   // ベルト（固定暗色）

            // ── 頭・帽子 ──
            Quad(p, Skin, 0.415f, 0.115f, 0.585f, 0.245f);
            Quad(p, cap, 0.40f, 0.075f, 0.60f, 0.135f);   // クラウン
            Quad(p, cap, 0.40f, 0.135f, 0.66f, 0.155f);   // つば（クラウンと同色＝購入素体は1メッシュ）
        }

        private void Quad(Painter2D p, Color color, float x0, float y0, float x1, float y1)
        {
            p.fillColor = color;
            p.BeginPath();
            p.MoveTo(N(x0, y0));
            p.LineTo(N(x1, y0));
            p.LineTo(N(x1, y1));
            p.LineTo(N(x0, y1));
            p.ClosePath();
            p.Fill();
        }

        /// <summary>
        /// 縦縞。実寸(本/m)を図の幅へ写す＝選手の肩幅を約0.45mとみなし、図の1.0が約1.25m（身長）に当たる。
        /// 線の太さは対決ビューと同じ実寸3.5mm相当だが、模式図では1px未満になると消えるので下限を張る。
        /// </summary>
        private void Stripes(Painter2D p, float x0, float y0, float x1, float y1)
        {
            const float FigureHeightM = 1.25f;   // 図の縦1.0が相当する実寸[m]
            float pitchNorm = 1f / ((float)_colors.Stripe.FreqPerMeter * FigureHeightM);
            if (pitchNorm <= 0.0005f) return;
            float lineNorm = Mathf.Max(0.0035f / FigureHeightM, 1.2f / _box.width);

            p.fillColor = C(_colors.Stripe.Color);
            for (float x = x0 + pitchNorm * 0.5f; x < x1; x += pitchNorm)
            {
                float xe = Mathf.Min(x + lineNorm, x1);
                p.BeginPath();
                p.MoveTo(N(x, y0));
                p.LineTo(N(xe, y0));
                p.LineTo(N(xe, y1));
                p.LineTo(N(x, y1));
                p.ClosePath();
                p.Fill();
            }
        }
    }
}

using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// ギザギザの叫び吹き出し（issue #696 で部品辞書に追加）。漫画の「！」の吹き出しのように、
    /// 縁がトゲトゲの多角形を Painter2D で描き、中に短い文言を1行だけ置く。試合盤面の一瞬の合図用
    /// （「走った！」等）で、データ画面には使わない。
    ///
    /// <para>色は USS のカスタムプロパティ（<c>--burst-fill</c> / <c>--burst-ink</c>）から取る＝画面側で直書きしない。
    /// 盤面では色は状態コード（走者＝アンバー）なので、既定の塗りは <c>--color-lamp</c> と同値。
    /// 形は要素の実寸から毎回組み直す（寸法をUSSの width/height で決めれば絵が追従する）。</para>
    ///
    /// <para>クリックは素通し（盤面クリック→中断ポップアップを塞がない）。</para>
    /// </summary>
    public sealed class BurstBalloon : VisualElement
    {
        // トゲの数と深さ。数は奇数にして左右対称に見えすぎないようにする（漫画の叫び吹き出しの崩し）。
        private const int Spikes = 13;
        private const float SpikeDepth = 0.22f;   // 外周に対する内側の頂点の比（0＝楕円、大きいほど尖る）
        private const float OutlineWidth = 3f;    // インク枠の太さ[px]（--nb-border-card と同じ3px）

        private Color _fill = new Color32(0xF5, 0xC6, 0x4A, 0xFF);   // --burst-fill（既定＝--color-lamp）
        private Color _ink = new Color32(0x13, 0x15, 0x12, 0xFF);    // --burst-ink（既定＝--ink）

        private static readonly CustomStyleProperty<Color> FillProp = new("--burst-fill");
        private static readonly CustomStyleProperty<Color> InkProp = new("--burst-ink");

        private readonly Label _text;

        public BurstBalloon(string text = "")
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("burst");
            _text = new Label(text) { pickingMode = PickingMode.Ignore };
            _text.AddToClassList("burst__text");
            _text.AddToClassList("f-display");
            Add(_text);
            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyles);
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
        }

        /// <summary>中の文言を差し替える（1行の短い叫びを想定）。</summary>
        public string Text
        {
            get => _text.text;
            set => _text.text = value;
        }

        private void OnStyles(CustomStyleResolvedEvent e)
        {
            if (e.customStyle.TryGetValue(FillProp, out var f)) _fill = f;
            if (e.customStyle.TryGetValue(InkProp, out var k)) _ink = k;
            MarkDirtyRepaint();
        }

        private void OnGenerate(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width <= 1f || r.height <= 1f) return;

            var c = r.center;
            var rx = r.width * 0.5f - OutlineWidth;
            var ry = r.height * 0.5f - OutlineWidth;
            var p = ctx.painter2D;
            p.BeginPath();
            for (var i = 0; i < Spikes * 2; i++)
            {
                // 外の頂点（トゲ先）と内の頂点を交互に置く。内の頂点は少しだけ揺らして手描きらしく崩す。
                var a = Mathf.PI * 2f * i / (Spikes * 2) - Mathf.PI * 0.5f;
                var outer = i % 2 == 0;
                var k = outer ? 1f : 1f - SpikeDepth * (0.85f + 0.3f * ((i * 7) % 5) / 4f);
                var pt = new Vector2(c.x + Mathf.Cos(a) * rx * k, c.y + Mathf.Sin(a) * ry * k);
                if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
            }
            p.ClosePath();
            p.fillColor = _fill;
            p.Fill();
            p.strokeColor = _ink;
            p.lineWidth = OutlineWidth;
            p.lineJoin = LineJoin.Miter;
            p.Stroke();
        }
    }
}

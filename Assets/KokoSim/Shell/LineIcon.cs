using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 線画アイコン（部品辞書）。SDFフォントに絵文字が無いので、24×24 の座標で組んだ線画を
    /// Painter2D で要素の大きさへ拡大して描く。線の色は要素の <c>color</c>（USS で継承される文字色）を使う
    /// ＝タイルの文字色を替えればアイコンの色も替わる。線幅は 24 座標で 2（角・端は丸め）。
    /// 寸法は USS（例 <c>.licon--22</c>）で与える。押せない＝当たり判定は親（ボタン）に任せる。
    /// </summary>
    public sealed class LineIcon : VisualElement
    {
        public enum Kind
        {
            /// <summary>拡声器（伝令を送る）。</summary>
            Megaphone,
            /// <summary>左右の矢印（選手交代）。</summary>
            Swap,
            /// <summary>人物＋名簿の線（選手能力）。</summary>
            Roster,
            /// <summary>早送り（スキップ）。</summary>
            FastForward,
            /// <summary>再生（塗り。再開）。</summary>
            Play,
            /// <summary>棒グラフ（ホーム：チーム成績の記録なし）。</summary>
            BarChart,
            /// <summary>トロフィー（ホーム：ランキングの記録なし）。</summary>
            Trophy,
            /// <summary>右向きの山形（行の飛び先）。</summary>
            ChevronRight,
            /// <summary>十字（追加）。</summary>
            Plus,
            /// <summary>右向きの矢印（打者を一塁へ歩かせる＝申告敬遠）。</summary>
            Walk,
            /// <summary>左向きの山形（戻る）。</summary>
            ChevronLeft,
            /// <summary>ゴミ箱（削除）。</summary>
            Trash,
        }

        /// <summary>線幅（24 座標で）。既定 2。</summary>
        public float Stroke { get; set; } = StrokeWidth;

        private const float ViewBox = 24f;
        private const float StrokeWidth = 2f;

        private Kind _kind;

        public LineIcon() : this(Kind.Play) { }

        public LineIcon(Kind kind)
        {
            _kind = kind;
            AddToClassList("licon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnGenerate;
            // 文字色（color）は継承で後から決まる／無効化で変わる＝スタイル解決のたびに描き直す。
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
        }

        public Kind IconKind
        {
            get => _kind;
            set { _kind = value; MarkDirtyRepaint(); }
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width <= 0f || r.height <= 0f) return;
            var s = Mathf.Min(r.width, r.height) / ViewBox;
            var o = new Vector2(r.x + (r.width - ViewBox * s) * 0.5f, r.y + (r.height - ViewBox * s) * 0.5f);
            Vector2 P(float x, float y) => o + new Vector2(x * s, y * s);

            var p = mgc.painter2D;
            var color = resolvedStyle.color;
            p.strokeColor = color;
            p.fillColor = color;
            p.lineWidth = Stroke * s;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            switch (_kind)
            {
                case Kind.Megaphone:
                    p.BeginPath();
                    p.MoveTo(P(3, 10)); p.LineTo(P(3, 14)); p.LineTo(P(6, 14)); p.LineTo(P(12, 18));
                    p.LineTo(P(12, 6)); p.LineTo(P(6, 10)); p.ClosePath();
                    p.Stroke();
                    // 音の弧2本（中心は弦の左。弦の半角 ≈ 45.6°）。
                    StrokeArc(p, P(13.55f, 12), 3.5f * s, -45.6f, 45.6f);
                    StrokeArc(p, P(14.1f, 12), 7f * s, -45.6f, 45.6f);
                    break;

                case Kind.Swap:
                    Polyline(p, P(7, 4), P(3, 8), P(7, 12));
                    Polyline(p, P(3, 8), P(17, 8));
                    Polyline(p, P(17, 12), P(21, 16), P(17, 20));
                    Polyline(p, P(21, 16), P(7, 16));
                    break;

                case Kind.Roster:
                    p.BeginPath();
                    p.Arc(P(9, 8), 3.5f * s, 0f, 360f);
                    p.Stroke();
                    StrokeArc(p, P(9, 20), 6f * s, 180f, 360f);   // 肩（上半円）
                    Polyline(p, P(16, 8), P(21, 8));
                    Polyline(p, P(16, 12), P(21, 12));
                    Polyline(p, P(18, 16), P(21, 16));
                    break;

                case Kind.FastForward:
                    p.BeginPath();
                    p.MoveTo(P(4, 6)); p.LineTo(P(11, 12)); p.LineTo(P(4, 18)); p.ClosePath();
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(P(13, 6)); p.LineTo(P(20, 12)); p.LineTo(P(13, 18)); p.ClosePath();
                    p.Stroke();
                    break;

                case Kind.BarChart:
                    Polyline(p, P(4, 20), P(4, 10));
                    Polyline(p, P(10, 20), P(10, 4));
                    Polyline(p, P(16, 20), P(16, 13));
                    Polyline(p, P(22, 20), P(2, 20));
                    break;

                case Kind.Trophy:
                    Polyline(p, P(8, 21), P(16, 21));
                    Polyline(p, P(12, 17), P(12, 21));
                    p.BeginPath();
                    p.MoveTo(P(7, 9)); p.LineTo(P(7, 4)); p.LineTo(P(17, 4)); p.LineTo(P(17, 9));
                    p.Arc(P(12, 9), 5f * s, 0f, 180f);
                    p.Stroke();
                    Polyline(p, P(7, 6), P(4, 6), P(4.4f, 8), P(5.5f, 9.3f), P(7, 10));
                    Polyline(p, P(17, 6), P(20, 6), P(19.6f, 8), P(18.5f, 9.3f), P(17, 10));
                    break;

                case Kind.ChevronRight:
                    Polyline(p, P(9, 6), P(15, 12), P(9, 18));
                    break;

                case Kind.ChevronLeft:
                    Polyline(p, P(15, 6), P(9, 12), P(15, 18));
                    break;

                case Kind.Trash:
                    Polyline(p, P(4, 7), P(20, 7));
                    Polyline(p, P(9, 7), P(9, 4), P(15, 4), P(15, 7));
                    Polyline(p, P(6, 7), P(7, 20), P(17, 20), P(18, 7));
                    Polyline(p, P(10, 11), P(10, 17));
                    Polyline(p, P(14, 11), P(14, 17));
                    break;

                case Kind.Walk:
                    Polyline(p, P(3, 12), P(20, 12));
                    Polyline(p, P(13, 5), P(20, 12), P(13, 19));
                    break;

                case Kind.Plus:
                    Polyline(p, P(12, 5), P(12, 19));
                    Polyline(p, P(5, 12), P(19, 12));
                    break;

                case Kind.Play:
                    p.BeginPath();
                    p.MoveTo(P(7, 4.5f)); p.LineTo(P(7, 19.5f)); p.LineTo(P(19.5f, 12)); p.ClosePath();
                    p.Fill();
                    break;
            }
        }

        private static void Polyline(Painter2D p, params Vector2[] pts)
        {
            p.BeginPath();
            p.MoveTo(pts[0]);
            for (var i = 1; i < pts.Length; i++) p.LineTo(pts[i]);
            p.Stroke();
        }

        private static void StrokeArc(Painter2D p, Vector2 c, float radius, float from, float to)
        {
            p.BeginPath();
            p.Arc(c, radius, from, to);
            p.Stroke();
        }
    }
}

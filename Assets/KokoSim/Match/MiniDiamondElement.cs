using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 守備フォーカス中に盤面の右下へ出す小ダイヤモンド（issue #595・監督選択 B案）。
    ///
    /// <para>守備カメラは球と野手に寄って追随するので、枠外へ出た走者は原理的に映らない
    /// ＝「三塁で止まったのか本塁を狙ったのか」が絵から読めない。ここは画面固定で
    /// <b>走者ドットと塁だけ</b>を描いてそれを補う。打球も野手も描かない＝2D俯瞰の縮小版にはしない
    /// （盤面の主役は3Dの打球処理のまま）。塁の在/不在は ScoreBug の塁ダイヤの担当なので役割を重ねない
    /// ＝こちらは<b>守備フォーカス中だけ</b>出る。</para>
    ///
    /// <para>面は掲示板の黒（<c>--sb-face</c>）＋インク枠＝design-19 の黒島語彙で、芝・土・空の
    /// どの背景の上でも沈まない。左上の ScoreBug と対角で対になる（黒島は画面に2つだけ）。
    /// 色は「状態コード」（走者＝アンバー）で、UI原則②の本数制限は盤面ビューには適用しない
    /// （CLAUDE.md「適用範囲の例外（試合盤面）」）。</para>
    ///
    /// <para>球場座標[m]→箱のローカル座標の写像は engine の <see cref="MiniDiamondProjection"/> が
    /// <b>単一ソース</b>で、塁マスも走者ドットも同じ関数を通る＝塁に居る走者は必ず塁マスの上に乗る。
    /// 走者位置そのものは <c>DuelStageRig.CurrentRunnerDots()</c>（＝3Dの走者と同じ
    /// <c>RunnerAt</c> を同じ時刻で評価）から受け取るだけで、補間はここに書かない。
    /// <b>アウトになった走者はその時点で渡って来なくなる＝ドットが消える</b>（2026-08-12 監督指示。
    /// 残すと生きている走者と区別が付かず、この図の目的そのものを潰す）。</para>
    /// </summary>
    public sealed class MiniDiamondElement : VisualElement
    {
        // ── 実寸[px]。箱の中の比率でなく実寸で持つ（枠が小さいので比率だと線が1px未満へ割れる） ──
        private const float BaseSide = 9f;      // 一・二・三塁のマス（正方形）
        private const float HomeSide = 9f;      // 本塁（五角形の外接幅）
        private const float RunnerRadius = 5.5f;
        private const float RunnerRing = 1.5f;  // 走者の黒縁（明るい面でも締まる）
        private const float PathWidth = 1f;     // 塁間の細線

        private Color _chalk = Hex(0xEFF4EA);   // 塁・塁間（--field-chalk）
        private Color _lamp = Hex(0xF5C64A);    // 走者（--color-lamp）
        private Color _ink = Hex(0x131512);     // 走者の縁（--ink）

        private static readonly CustomStyleProperty<Color> ChalkProp = new("--field-chalk");
        private static readonly CustomStyleProperty<Color> LampProp = new("--color-lamp");
        private static readonly CustomStyleProperty<Color> InkProp = new("--ink");

        private static readonly Vector2[] BasesM =
        {
            V2(FieldDiagramGeometry.First), V2(FieldDiagramGeometry.Second), V2(FieldDiagramGeometry.Third),
        };

        private readonly List<Vector2> _runners = new();

        public MiniDiamondElement()
        {
            pickingMode = PickingMode.Ignore;   // 盤面クリック（→中断ポップアップ）を塞がない
            AddToClassList("mdia");
            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyles);
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
        }

        /// <summary>
        /// 走者ドットを差し替える（球場座標[m]）。中身が1つも変わっていなければ再描画しない
        /// ＝止まっている走者だけの間は Painter2D を回さない。
        /// </summary>
        public void SetRunners(IReadOnlyList<DuelStageRig.RunnerDot> dots)
        {
            var changed = dots.Count != _runners.Count;
            if (!changed)
            {
                for (var i = 0; i < dots.Count; i++)
                {
                    if (_runners[i] == dots[i].FieldM) continue;
                    changed = true;
                    break;
                }
            }
            if (!changed) return;

            _runners.Clear();
            for (var i = 0; i < dots.Count; i++) _runners.Add(dots[i].FieldM);
            MarkDirtyRepaint();
        }

        private void OnStyles(CustomStyleResolvedEvent e)
        {
            var cs = e.customStyle;
            if (cs.TryGetValue(ChalkProp, out var c)) _chalk = c;
            if (cs.TryGetValue(LampProp, out var l)) _lamp = l;
            if (cs.TryGetValue(InkProp, out var i)) _ink = i;
            MarkDirtyRepaint();
        }

        /// <summary>球場座標[m] → 要素ローカル座標[px]。写像は engine の単一ソースを通す。</summary>
        private Vector2 FieldToLocal(Vector2 m)
        {
            var r = contentRect;
            var (u, v) = MiniDiamondProjection.ToLocal01(m.x, m.y);
            return new Vector2(r.x + (float)u * r.width, r.y + (float)v * r.height);
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width < 8f || r.height < 8f) return;   // レイアウト未確定・極小時は描かない
            var p = mgc.painter2D;

            var home = FieldToLocal(V2(FieldDiagramGeometry.Home));
            var first = FieldToLocal(BasesM[0]);
            var second = FieldToLocal(BasesM[1]);
            var third = FieldToLocal(BasesM[2]);

            // 1. 塁間（走者がどの区間に居るかを読ませる線）。塁より一段暗くして塁マスを主にする。
            p.strokeColor = new Color(_chalk.r, _chalk.g, _chalk.b, 0.38f);
            p.lineWidth = PathWidth;
            p.BeginPath();
            p.MoveTo(home);
            p.LineTo(first);
            p.LineTo(second);
            p.LineTo(third);
            p.ClosePath();
            p.Stroke();

            // 2. 塁（一・二・三＝正方形／本塁＝五角形）。
            p.fillColor = _chalk;
            foreach (var b in BasesM) FillSquare(p, FieldToLocal(b), BaseSide);
            FillHomePlate(p, home, HomeSide);

            // 3. 走者（アンバー＋黒縁）。塁の上に重ねる＝「その塁に居る」が一目で分かる。
            foreach (var m in _runners)
            {
                var c = FieldToLocal(m);
                p.fillColor = _ink;
                FillCircle(p, c, RunnerRadius + RunnerRing);
                p.fillColor = _lamp;
                FillCircle(p, c, RunnerRadius);
            }
        }

        private static void FillSquare(Painter2D p, Vector2 c, float side)
        {
            var h = side * 0.5f;
            p.BeginPath();
            p.MoveTo(new Vector2(c.x - h, c.y - h));
            p.LineTo(new Vector2(c.x + h, c.y - h));
            p.LineTo(new Vector2(c.x + h, c.y + h));
            p.LineTo(new Vector2(c.x - h, c.y + h));
            p.ClosePath();
            p.Fill();
        }

        /// <summary>本塁（五角形）。他の3つと形を変えて「図の下＝本塁」を字なしで示す。</summary>
        private static void FillHomePlate(Painter2D p, Vector2 c, float side)
        {
            var h = side * 0.5f;
            p.BeginPath();
            p.MoveTo(new Vector2(c.x - h, c.y - h));
            p.LineTo(new Vector2(c.x + h, c.y - h));
            p.LineTo(new Vector2(c.x + h, c.y + h * 0.2f));
            p.LineTo(new Vector2(c.x, c.y + h));
            p.LineTo(new Vector2(c.x - h, c.y + h * 0.2f));
            p.ClosePath();
            p.Fill();
        }

        private static void FillCircle(Painter2D p, Vector2 c, float radius)
        {
            p.BeginPath();
            p.Arc(c, radius, 0f, 360f);
            p.ClosePath();
            p.Fill();
        }

        private static Vector2 V2((double X, double Y) t) => new((float)t.X, (float)t.Y);

        private static Color Hex(uint rgb) => new(
            ((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}

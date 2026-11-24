using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Unity.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 対決ビューの右の中ほどに出す投球チャート（issue #1541・2026-11-23/24 ユーザー決定）。テレビ中継の左下の図と同じく、
    /// <b>今の打席</b>で投げた球のコースを、ストライクゾーン（9分割）の上に番号付きの印で打っていく。
    ///
    /// <list type="bullet">
    /// <item>向き: 投手の後ろから見た側（対決ビューの絵と同じ）。写像は engine の
    /// <see cref="PitchChartProjection"/> が単一ソースで、枠も印も同じ関数を通る。</item>
    /// <item>色＝結果: 赤＝ストライク（見逃し・空振り）／緑＝ボール・死球／黄＝ファウル／青＝インプレー。
    /// 盤面ビューの状態コードなので UI原則②の本数制限は適用しない（CLAUDE.md「適用範囲の例外（試合盤面）」）。
    /// 赤は CTA でなく中継の記号としてユーザーが指定した。色は ScoreBug の灯り（--lamp-*）と同じトークン。</item>
    /// <item>形＝球種: ストレートは丸。変化球は三角で、頂点を球種チャートと同じ向き
    /// （<see cref="PitchTypeStyle.AngleFor"/>＝投手の後ろから見た向き・左投手は左右反転）へ向ける。</item>
    /// <item>印の中に、その打席の何球目か。投手名・球数・見出しは出さない。</item>
    /// </list>
    ///
    /// <para>面は ScoreBug・小ダイヤと同じ黒島（<c>--sb-face</c>＋インク枠）。番号は Label（f-num）を印の中心へ重ねる
    /// ＝Painter2D で文字を描かない。コースの無い球（合成の球）は呼び出し側が渡さない＝架空のコースを描かない。</para>
    /// </summary>
    public sealed class PitchLocationChartElement : VisualElement
    {
        /// <summary>印の色の区分（結果）。</summary>
        public enum MarkResult { Strike, Ball, Foul, InPlay }

        private readonly struct Mark
        {
            public readonly double X, Y;          // 本塁面[m]（+X=一塁側）
            public readonly MarkResult Result;
            public readonly bool Breaking;        // 変化球＝三角
            public readonly float AngleDeg;       // 三角の頂点の向き（右=0°・上=90°・投手の後ろから見た向き）
            public readonly Label Number;

            public Mark(double x, double y, MarkResult r, bool breaking, float angle, Label number)
            {
                X = x; Y = y; Result = r; Breaking = breaking; AngleDeg = angle; Number = number;
            }
        }

        // ── 実寸[px]（箱が小さいので比率でなく実寸で持つ＝線が1px未満に割れない） ──
        private const float MarkRadius = 10f;      // 丸の半径（9分割の1マスに収まる大きさ）
        private const float TriRadius = 15f;       // 三角の外接円の半径（内接円が番号1桁ぶん＝丸より大きく取らないと番号がはみ出す）
        private const float MarkRing = 1.5f;       // 印の黒縁（明るい色でも締まる）
        private const float ZoneLine = 2f;         // ゾーンの外枠
        private const float GridLine = 1f;         // 9分割の内側の線

        private Color _chalk = Hex(0xEFF4EA);      // --field-chalk
        private Color _ink = Hex(0x131512);        // --ink
        private Color _strike = Hex(0xFF4B3E);     // --lamp-o
        private Color _ball = Hex(0x3DDC6B);       // --lamp-b
        private Color _foul = Hex(0xFFAE1A);       // --lamp-s
        private Color _inPlay = Hex(0x5EC8FF);     // --sb-call-hit

        private static readonly CustomStyleProperty<Color> ChalkProp = new("--field-chalk");
        private static readonly CustomStyleProperty<Color> InkProp = new("--ink");
        private static readonly CustomStyleProperty<Color> StrikeProp = new("--lamp-o");
        private static readonly CustomStyleProperty<Color> BallProp = new("--lamp-b");
        private static readonly CustomStyleProperty<Color> FoulProp = new("--lamp-s");
        private static readonly CustomStyleProperty<Color> InPlayProp = new("--sb-call-hit");

        /// <summary>
        /// ゾーンの寸法。判定に使われている engine の <see cref="StrikeZone"/>（3D のゾーン表示
        /// <c>DuelStageRig.Zone</c> と同じ既定。試合ごとに差し替える経路は無い）。
        /// </summary>
        private static readonly StrikeZone Zone = new();

        private readonly List<Mark> _marks = new();

        public PitchLocationChartElement()
        {
            pickingMode = PickingMode.Ignore;   // 盤面クリック（→中断ポップアップ）を塞がない
            AddToClassList("plchart");
            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyles);
            RegisterCallback<GeometryChangedEvent>(_ => { PlaceNumbers(); MarkDirtyRepaint(); });
        }

        /// <summary>打った印の数。</summary>
        public int Count => _marks.Count;

        /// <summary>印を全部消す（打者が替わったとき）。</summary>
        public void ClearMarks()
        {
            if (_marks.Count == 0) return;
            foreach (var m in _marks) m.Number.RemoveFromHierarchy();
            _marks.Clear();
            MarkDirtyRepaint();
        }

        /// <summary>
        /// 1球ぶんの印を足す。<paramref name="number"/> はその打席の何球目か（1始まり）。
        /// <paramref name="type"/> が null（球種の記録が無い球）は丸で打つ。
        /// </summary>
        public void AddMark(double x, double y, MarkResult result, PitchType? type, Handedness throws, int number)
        {
            var breaking = type.HasValue && type.Value != PitchType.Fastball;
            var angle = type.HasValue ? PitchTypeStyle.AngleFor(type.Value, throws) : 90f;
            var label = new Label(number.ToString()) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("plchart__num");
            label.AddToClassList("f-num-bd");
            Add(label);
            _marks.Add(new Mark(x, y, result, breaking, angle, label));
            PlaceNumbers();
            MarkDirtyRepaint();
        }

        /// <summary><see cref="PitchKind"/> → 印の色の区分（死球はボールと同じ緑・2026-11-24 ユーザー決定）。</summary>
        public static MarkResult ResultOf(PitchKind kind) => kind switch
        {
            PitchKind.CalledStrike or PitchKind.SwingingStrike => MarkResult.Strike,
            PitchKind.Foul => MarkResult.Foul,
            PitchKind.InPlay => MarkResult.InPlay,
            _ => MarkResult.Ball,   // Ball / HitByPitch
        };

        private void OnStyles(CustomStyleResolvedEvent e)
        {
            var cs = e.customStyle;
            if (cs.TryGetValue(ChalkProp, out var c)) _chalk = c;
            if (cs.TryGetValue(InkProp, out var i)) _ink = i;
            if (cs.TryGetValue(StrikeProp, out var s)) _strike = s;
            if (cs.TryGetValue(BallProp, out var b)) _ball = b;
            if (cs.TryGetValue(FoulProp, out var f)) _foul = f;
            if (cs.TryGetValue(InPlayProp, out var p)) _inPlay = p;
            MarkDirtyRepaint();
        }

        /// <summary>
        /// 図の描画先（中身の矩形に、図の縦横比を保ったまま収めた矩形）。縦横比を崩すと、ゾーンの枠と
        /// 9分割が実物の比率から歪む。
        /// </summary>
        private Rect Plot()
        {
            var r = contentRect;
            var aspect = (float)(PitchChartProjection.SpanXM(Zone) / PitchChartProjection.SpanYM(Zone));
            var w = Mathf.Min(r.width, r.height * aspect);
            var h = w / aspect;
            return new Rect(r.x + (r.width - w) / 2f, r.y + (r.height - h) / 2f, w, h);
        }

        private Vector2 ToLocal(Rect plot, double x, double y)
        {
            var (u, v) = PitchChartProjection.ToLocal01(x, y, Zone);
            return new Vector2(plot.x + (float)u * plot.width, plot.y + (float)v * plot.height);
        }

        private void PlaceNumbers()
        {
            var plot = Plot();
            if (plot.width < 8f) return;
            // 絶対配置の left/top は枠の内側が原点。印の描画（Painter2D）と contentRect は枠の外側が原点なので、枠の太さぶん引く。
            var origin = new Vector2(resolvedStyle.borderLeftWidth, resolvedStyle.borderTopWidth);
            foreach (var m in _marks)
            {
                var c = ToLocal(plot, m.X, m.Y) - origin;
                // ラベルの箱を印の中心へ（幅は文字で決まる＝translate で中心合わせ）。
                m.Number.style.left = c.x;
                m.Number.style.top = c.y - DigitDropPx(m.Number);
            }
        }

        /// <summary>
        /// 行の箱の中心から数字の字面の中心までの下がり[px]（箱は上に空きが多く、数字は基線から字高ぶんしか立たない）。
        /// 書体の縦メトリクス（箱の中心＝(アセンダ＋ディセンダ)/2・数字の中心＝字高/2）から出すので書体・サイズが変わっても追従する。
        /// </summary>
        private static float DigitDropPx(Label label)
        {
            var asset = label.resolvedStyle.unityFontDefinition.fontAsset;
            if (asset == null) return 0f;
            var f = asset.faceInfo;
            if (f.pointSize <= 0f) return 0f;
            var boxCenter = (f.ascentLine + f.descentLine) * 0.5f;   // descentLine は負
            var digitCenter = f.capLine * 0.5f;
            return (boxCenter - digitCenter) / f.pointSize * label.resolvedStyle.fontSize;
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var plot = Plot();
            if (plot.width < 8f || plot.height < 8f) return;   // レイアウト未確定・極小時は描かない
            var p = mgc.painter2D;

            // 1. ゾーン（外枠＋9分割）。内側の線は外枠より細く淡くして、枠を主にする。
            var (u0, v0, u1, v1) = PitchChartProjection.ZoneLocal01(Zone);
            var tl = new Vector2(plot.x + (float)u0 * plot.width, plot.y + (float)v0 * plot.height);
            var br = new Vector2(plot.x + (float)u1 * plot.width, plot.y + (float)v1 * plot.height);
            var w3 = (br.x - tl.x) / 3f;
            var h3 = (br.y - tl.y) / 3f;
            p.strokeColor = new Color(_chalk.r, _chalk.g, _chalk.b, 0.45f);
            p.lineWidth = GridLine;
            for (var k = 1; k <= 2; k++)
            {
                Line(p, new Vector2(tl.x + w3 * k, tl.y), new Vector2(tl.x + w3 * k, br.y));
                Line(p, new Vector2(tl.x, tl.y + h3 * k), new Vector2(br.x, tl.y + h3 * k));
            }
            p.strokeColor = _chalk;
            p.lineWidth = ZoneLine;
            p.BeginPath();
            p.MoveTo(tl);
            p.LineTo(new Vector2(br.x, tl.y));
            p.LineTo(br);
            p.LineTo(new Vector2(tl.x, br.y));
            p.ClosePath();
            p.Stroke();

            // 2. 印（古い順に描く＝新しい球が上に乗る）。
            foreach (var m in _marks)
            {
                var c = ToLocal(plot, m.X, m.Y);
                var col = ColorOf(m.Result);
                if (m.Breaking)
                {
                    p.fillColor = _ink;
                    FillTriangle(p, c, TriRadius + MarkRing * 2f, m.AngleDeg);
                    p.fillColor = col;
                    FillTriangle(p, c, TriRadius, m.AngleDeg);
                }
                else
                {
                    p.fillColor = _ink;
                    FillCircle(p, c, MarkRadius + MarkRing);
                    p.fillColor = col;
                    FillCircle(p, c, MarkRadius);
                }
            }
        }

        private Color ColorOf(MarkResult r) => r switch
        {
            MarkResult.Strike => _strike,
            MarkResult.Foul => _foul,
            MarkResult.InPlay => _inPlay,
            _ => _ball,
        };

        private static void Line(Painter2D p, Vector2 a, Vector2 b)
        {
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.Stroke();
        }

        private static void FillCircle(Painter2D p, Vector2 c, float r)
        {
            p.BeginPath();
            p.Arc(c, r, 0f, 360f);
            p.ClosePath();
            p.Fill();
        }

        /// <summary>正三角形。頂点を <paramref name="angleDeg"/>（右=0°・上=90°）へ向ける。画面座標は y が下向き。</summary>
        private static void FillTriangle(Painter2D p, Vector2 c, float r, float angleDeg)
        {
            p.BeginPath();
            for (var k = 0; k < 3; k++)
            {
                var a = (angleDeg + 120f * k) * Mathf.Deg2Rad;
                var v = new Vector2(c.x + r * Mathf.Cos(a), c.y - r * Mathf.Sin(a));
                if (k == 0) p.MoveTo(v); else p.LineTo(v);
            }
            p.ClosePath();
            p.Fill();
        }

        private static Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}

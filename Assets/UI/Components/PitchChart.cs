using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Players;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 球種チャート1列ぶんの入力（1球種＝中心から伸びる丸の列1本）。
    /// 部品辞書の <see cref="PitchChartView"/> が読む共通データ型（issue #94: 画面ローカル実装からの昇格）。
    /// </summary>
    public sealed class PitchChartDatum
    {
        public PitchType Type;
        public string Name = "";
        // 略記（「チェ」）。compact 版だけがこちらを使う（正式名は狭い器に収まらない, issue #560）。空なら Name を出す。
        public string ShortName = "";
        public string Kire = "C";     // 有効軸ランク（ストレート=球威/Stuff、変化球=キレ/Sharpness）→S〜G。丸の数とチップ（Issue #274）
        public float AngleDeg;        // 変化の向き（右=0°・上=90°・反時計回り）。投げ手の反転は済ませた値（PitchTypeStyle.AngleFor）
        public bool IsFastball;       // ストレートはチップを出さず最速を添える
        public string VeloText = "";  // ストレートに添える最速（"148"。null/空で出さない）
    }

    /// <summary>
    /// 球種ごとの向きと色（球種チャートの単一ソース）。角度は投手の後ろから見た右投手の向き（右=0°・上=90°）。
    /// 左投手は左右を反転する（180°−角度）。色は投げ手によらない。
    /// </summary>
    public static class PitchTypeStyle
    {
        private struct Entry
        {
            public float Angle; public Color Dot; public Color Text;
            public Entry(float a, string dot, string text) { Angle = a; Dot = Hex(dot); Text = Hex(text); }
        }

        private static readonly Dictionary<PitchType, Entry> Table = new Dictionary<PitchType, Entry>
        {
            { PitchType.Fastball, new Entry(90f,  "#e0533f", "#a8321f") },
            { PitchType.TwoSeam,  new Entry(30f,  "#e8873a", "#9a4f12") },
            { PitchType.Shuuto,   new Entry(0f,   "#e3a817", "#8a6200") },
            { PitchType.Sinker,   new Entry(320f, "#3fa877", "#1f6b47") },
            { PitchType.Changeup, new Entry(295f, "#9c7a54", "#604523") },
            { PitchType.Fork,     new Entry(270f, "#2f9bb0", "#16606e") },
            { PitchType.Curve,    new Entry(215f, "#4a7fd0", "#24508f") },
            { PitchType.Slider,   new Entry(180f, "#7b63d6", "#4a3799") },
            { PitchType.Cutter,   new Entry(145f, "#c45a9a", "#863565") },
        };

        private static readonly Entry Fallback = new Entry(90f, "#8a8478", "#55524b");

        /// <summary>投げ手を反映した向き[deg]（右=0°・上=90°）。左投手は 180°−角度。</summary>
        public static float AngleFor(PitchType type, Handedness throws)
        {
            var a = Get(type).Angle;
            return throws == Handedness.Left ? Normalize(180f - a) : a;
        }

        public static Color DotColor(PitchType type) => Get(type).Dot;
        public static Color TextColor(PitchType type) => Get(type).Text;

        private static Entry Get(PitchType t) => Table.TryGetValue(t, out var e) ? e : Fallback;

        private static float Normalize(float a) { a %= 360f; return a < 0f ? a + 360f : a; }

        private static Color Hex(string h) => ColorUtility.TryParseHtmlString(h, out var c) ? c : Color.gray;
    }

    /// <summary>
    /// 球種チャートの描画部品（部品辞書・UI原則⑤）。球種ごとに中心のボールから丸の列を伸ばす
    /// （丸の数＝ランク G1〜S8・同じ大きさ・等間隔・内側ほど薄い）。球種名とランクのチップは列の先の外側へ置く。
    /// 寸法の基準は選手画面の 480×310（中心 244,150）。小さい器（<paramref name="compact"/>＝比較カード・試合中の偵察/交代）
    /// では同じ描き方のまま全体を縮め、札が器・ボール・丸・ほかの札に重なるときだけ列の先の横/上/下へ逃がす。
    /// </summary>
    public sealed class PitchChartView
    {
        // ---- 基準寸法（scale=1・選手画面 480×310） ----
        private const float RefW = 480f, RefH = 310f, RefCx = 244f, RefCy = 150f;
        private const float DotR = 4f;            // 丸の半径（直径8）
        private const float FirstDot = 26f;       // 1つ目の丸の中心
        private const float DotStep = 11f;        // 丸の間隔（8つ目の中心＝103）
        private const float LabelGap = 15f;       // 一番外側の丸の中心から札の基準点まで
        private const float GuideOuter = 104f;    // 点線の円（Sの先端）
        private const float GuideInner = 50f;     // 実線の円
        private const float BallSize = 34f;
        private const float Edge = 0.35f;         // 札の寄せ方を切り替える cos/sin のしきい値

        private static readonly Color GuideOuterCol = new Color32(0xdd, 0xd7, 0xcb, 0xff);
        private static readonly Color GuideInnerCol = new Color32(0xef, 0xeb, 0xe3, 0xff);
        private static readonly Color ShadowCol = new Color32(0x1d, 0x1b, 0x18, 0xff);

        private readonly VisualElement _host;
        private readonly bool _compact;
        private readonly List<PitchChartDatum> _pitches = new List<PitchChartDatum>();
        private readonly List<VisualElement> _chips = new List<VisualElement>();
        // ストレートの最速（札の中の km/h）。この中心を丸の列の真上にそろえる。持たない札は null。
        private readonly List<VisualElement> _velos = new List<VisualElement>();
        private VisualElement _ball;

        // Layout で確定し OnPaint が読む。
        private Vector2 _center;
        private float _s = 1f;
        private bool _ready;

        public PitchChartView(VisualElement host, bool compact = false)
        {
            _host = host;
            _compact = compact;
            if (_host == null) return;
            _host.AddToClassList("pchart");
            if (compact) _host.AddToClassList("pchart--compact");
            _host.generateVisualContent += OnPaint;
            _host.RegisterCallback<GeometryChangedEvent>(_ => Layout());
        }

        /// <summary>表示する持ち球を差し替える（空/nullでチャートを隠す）。</summary>
        public void SetData(IReadOnlyList<PitchChartDatum> pitches)
        {
            _host?.Clear();
            _chips.Clear();
            _velos.Clear();
            _pitches.Clear();
            _ready = false;
            var has = pitches != null && pitches.Count > 0;
            if (_host != null) _host.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (!has) { _host?.MarkDirtyRepaint(); return; }

            _pitches.AddRange(pitches);

            // ボールは丸（generateVisualContent＝子より奥）の上・札の下に置く＝子の先頭。
            _ball = new VisualElement { pickingMode = PickingMode.Ignore };
            _ball.AddToClassList("pchart__ball");
            _host.Add(_ball);

            foreach (var pt in _pitches)
            {
                var chip = new VisualElement { pickingMode = PickingMode.Ignore };
                chip.AddToClassList("pd2-pchip");
                if (_compact) chip.AddToClassList("pd2-pchip--compact");
                // 寸法が確定するまで見せない（初回フレームに 0,0 へ出てから跳ぶのを防ぐ）。
                chip.style.visibility = Visibility.Hidden;

                // 小型版のストレートは球種名を出さず km/h だけ（issue #560: 器が狭く札が並ばない）。
                var veloOnly = _compact && pt.IsFastball && !string.IsNullOrEmpty(pt.VeloText);
                if (!veloOnly)
                {
                    var text = _compact && !string.IsNullOrEmpty(pt.ShortName) ? pt.ShortName : pt.Name;
                    var name = new Label(text);
                    name.AddToClassList("pd2-pchip__name");
                    name.style.color = PitchTypeStyle.TextColor(pt.Type);
                    chip.Add(name);
                }
                // ストレートはチップの代わりに最速（球威は能力表で見せている）。
                VisualElement velo = null;
                if (pt.IsFastball)
                {
                    if (!string.IsNullOrEmpty(pt.VeloText))
                    {
                        velo = UiComponents.NumUnit(pt.VeloText, "km/h", true, "pd2-pchip__velo");
                        chip.Add(velo);
                    }
                }
                else
                {
                    chip.Add(UiComponents.RankChip(pt.Kire, RankChipSize.Small));
                }
                // 札の実寸が確定したら置き直す（ホスト寸法が変わらない差し替えでは host 側の event が来ない）。
                chip.RegisterCallback<GeometryChangedEvent>(_ => Layout());
                _host.Add(chip);
                _chips.Add(chip);
                _velos.Add(velo);
            }
            Layout();
            _host.MarkDirtyRepaint();
        }

        /// <summary>ランク → 丸の数（G=1 … S=8）。</summary>
        public static int DotCount(string grade)
        {
            switch (grade)
            {
                case "S": return 8;
                case "A": return 7;
                case "B": return 6;
                case "C": return 5;
                case "D": return 4;
                case "E": return 3;
                case "F": return 2;
                default:  return 1;
            }
        }

        private static Vector2 Dir(float angleDeg)
        {
            var r = angleDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(r), -Mathf.Sin(r)); // 画面は +y が下
        }

        private float Outer(PitchChartDatum pt) => (FirstDot + DotStep * (DotCount(pt.Kire) - 1)) * _s;

        // 中心と縮尺を決め、ボールと札を置く。
        private void Layout()
        {
            if (_host == null || _pitches.Count == 0) return;
            var rect = _host.contentRect;
            if (rect.width < 40f || rect.height < 40f) return;
            UnityEngine.Debug.Log("[PCHART-DBG] Layout " + UnityEngine.Time.frameCount);

            // 札の実寸（位置によらない）。どれか1枚でも未確定なら、確定後の GeometryChangedEvent でやり直す。
            var sizes = new List<Vector2>(_chips.Count);
            var focus = new List<float>(_chips.Count);   // 札の中で列の真上に来るべき x（無ければ NaN＝札の中央）
            var sizesReady = true;
            for (var i = 0; i < _chips.Count; i++)
            {
                var chip = _chips[i];
                var w = chip.resolvedStyle.width;
                var h = chip.resolvedStyle.height;
                if (float.IsNaN(w) || float.IsNaN(h) || w < 1f || h < 1f) { sizesReady = false; w = 0f; h = 0f; }
                sizes.Add(new Vector2(w, h));
                var v = _velos[i];
                var vx = v != null ? v.layout.x + v.layout.width * 0.5f : float.NaN;
                focus.Add(float.IsNaN(vx) || v.layout.width < 1f ? float.NaN : vx);
            }

            if (_compact)
            {
                // 丸の列（S＝半径103＋丸の半径）が器の高さに収まる縮尺。
                _s = Mathf.Clamp((Mathf.Min(rect.width, rect.height) * 0.5f - 3f) / (FirstDot + DotStep * 7f + DotR), 0.3f, 1f);
                // 上向きの列（ストレート）の札は列の真上に置きたい＝その分の高さを上に確保する。
                // 下向きの列の丸が器に収まるよう縮尺を締め、中心を下げる（下向きの札は配置で逃がす）。
                float topRef = 0f, botRef = 0f, labelH = 0f;
                for (var i = 0; i < _pitches.Count; i++)
                {
                    var u = Dir(_pitches[i].AngleDeg);
                    var reach = FirstDot + DotStep * (DotCount(_pitches[i].Kire) - 1);
                    if (-u.y > Edge)
                    {
                        topRef = Mathf.Max(topRef, (reach + LabelGap) * -u.y);
                        labelH = Mathf.Max(labelH, sizes[i].y);
                    }
                    if (u.y > 0f) botRef = Mathf.Max(botRef, reach * u.y + DotR);
                }
                if (topRef > 0f)
                    _s = Mathf.Clamp(Mathf.Min(_s, (rect.height - labelH - 2f) / (topRef + botRef)), 0.3f, 1f);
                var cy = Mathf.Max(rect.height * 0.5f, topRef * _s + labelH + 1f);
                _center = new Vector2(rect.width * 0.5f, cy);
            }
            else
            {
                _s = Mathf.Min(1f, Mathf.Min(rect.width / RefW, rect.height / RefH));
                _center = new Vector2(rect.width * (RefCx / RefW), rect.height * (RefCy / RefH));
            }

            var ballSize = Mathf.Max(16f, BallSize * _s);
            _ball.style.position = Position.Absolute;
            _ball.style.width = ballSize;
            _ball.style.height = ballSize;
            _ball.style.left = _center.x - ballSize * 0.5f;
            _ball.style.top = _center.y - ballSize * 0.5f;

            if (!sizesReady) { _ready = true; _host.MarkDirtyRepaint(); return; }

            var obstacles = BuildObstacles(ballSize);
            var placed = new List<Rect>(_chips.Count);
            for (var i = 0; i < _chips.Count; i++)
            {
                var box = PlaceLabel(_pitches[i], sizes[i], focus[i], rect, obstacles, placed);
                placed.Add(box);
                _chips[i].style.position = Position.Absolute;
                _chips[i].style.left = box.x;
                _chips[i].style.top = box.y;
                _chips[i].style.visibility = Visibility.Visible;
            }
            _ready = true;
            _host.MarkDirtyRepaint();
        }

        // 札が避けたいもの（ボール・すべての丸）。
        private List<Rect> BuildObstacles(float ballSize)
        {
            var list = new List<Rect>();
            var br = ballSize * 0.5f + 2f;
            list.Add(new Rect(_center.x - br, _center.y - br, br * 2f, br * 2f));
            var r = DotRadius() + 1f;
            foreach (var pt in _pitches)
            {
                var u = Dir(pt.AngleDeg);
                var n = DotCount(pt.Kire);
                for (var k = 0; k < n; k++)
                {
                    var p = _center + u * ((FirstDot + DotStep * k) * _s);
                    list.Add(new Rect(p.x - r, p.y - r, r * 2f, r * 2f));
                }
            }
            return list;
        }

        private float DotRadius() => Mathf.Max(2f, DotR * _s);

        /// <summary>
        /// 札の置き場所を決める。第一候補は指示どおりの位置（一番外側の丸から15px先の基準点に、向きで寄せ方を決める）。
        /// それが器からはみ出す・ボールや丸やほかの札に重なるときだけ、列の先の横/上/下の候補と比べて一番重ならない所を選ぶ。
        /// 位置は整数に丸める（文字の滲み防止。丸は小数のまま描く）。
        /// </summary>
        private Rect PlaceLabel(PitchChartDatum pt, Vector2 size, float focusX, Rect rect, List<Rect> obstacles, List<Rect> placed)
        {
            var u = Dir(pt.AngleDeg);
            var outer = Outer(pt);
            var cos = u.x;
            var sin = -u.y;

            var anchor = _center + u * (outer + LabelGap * _s);
            var ax = cos < -Edge ? -1f : cos > Edge ? 0f : -0.5f;
            var ay = sin > Edge ? -1f : sin < -Edge ? 0f : -0.5f;
            var spec = new Rect(anchor.x + ax * size.x, anchor.y + ay * size.y, size.x, size.y);
            // ストレートの最速は丸の列の真上に中央そろえ（球種名はその左に並ぶ）。
            if (!float.IsNaN(focusX) && Mathf.Abs(cos) <= Edge) spec.x = anchor.x - focusX;

            var tip = _center + u * (outer + DotRadius() + 3f);
            var gap = 3f;
            // 基本の5か所（指示どおり／列の先の右・左・上・下）と、それぞれを札1枚ぶん上下・半分左右へずらした所。
            var bases = new[]
            {
                spec,
                new Rect(tip.x + gap, tip.y - size.y * 0.5f, size.x, size.y),              // 右
                new Rect(tip.x - gap - size.x, tip.y - size.y * 0.5f, size.x, size.y),     // 左
                new Rect(tip.x - size.x * 0.5f, tip.y - gap - size.y, size.x, size.y),     // 上
                new Rect(tip.x - size.x * 0.5f, tip.y + gap, size.x, size.y),              // 下
            };
            var shifts = new[]
            {
                Vector2.zero,
                new Vector2(0f, -(size.y + 2f)), new Vector2(0f, size.y + 2f),
                new Vector2(-size.x * 0.5f, 0f), new Vector2(size.x * 0.5f, 0f),
            };

            var specDist = Vector2.Distance(spec.center, tip);
            var best = spec;
            var bestScore = float.MaxValue;
            for (var b = 0; b < bases.Length; b++)
            for (var k = 0; k < shifts.Length; k++)
            {
                var raw = bases[b];
                raw.position += shifts[k];
                var box = Clamp(raw, rect);
                var score = (b == 0 ? 0f : 40f) + (k == 0 ? 0f : 25f);   // 指示どおりの位置を優先
                score += (Mathf.Abs(box.x - raw.x) + Mathf.Abs(box.y - raw.y)) * 2f; // 器へ押し戻した量
                var own = Vector2.Distance(box.center, tip);
                score += Mathf.Max(0f, own - specDist) * 0.5f;                       // 自分の列から離れすぎない
                // ほかの球種の列の先のほうが近い所は、どの列の札か読み違えるので強く避ける。
                // 指示どおりの位置（b=0,k=0）は対象外＝大きい版の見た目はモックのまま。
                foreach (var other in _pitches)
                {
                    if (b == 0 && k == 0) break;
                    if (ReferenceEquals(other, pt)) continue;
                    var ot = _center + Dir(other.AngleDeg) * (Outer(other) + DotRadius() + 3f);
                    var d = Vector2.Distance(box.center, ot);
                    if (d < own) score += (own - d) * 4f + 30f;
                }
                foreach (var o in obstacles) score += Overlap(box, o) * 3f;
                foreach (var p in placed) score += Overlap(box, p) * 20f;            // 札どうしの重なりは最も避ける
                if (score < bestScore) { bestScore = score; best = box; }
            }
            return new Rect(Mathf.Round(best.x), Mathf.Round(best.y), best.width, best.height);
        }

        private static float Overlap(Rect a, Rect b)
        {
            var w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
            var h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            return w > 0f && h > 0f ? w * h : 0f;
        }

        private static Rect Clamp(Rect b, Rect rect)
        {
            b.x = Mathf.Clamp(b.x, 0f, Mathf.Max(0f, rect.width - b.width));
            b.y = Mathf.Clamp(b.y, 0f, Mathf.Max(0f, rect.height - b.height));
            return b;
        }

        private void OnPaint(MeshGenerationContext ctx)
        {
            if (!_ready || _pitches.Count == 0) return;
            var p = ctx.painter2D;
            var c = _center;

            // ガイドの円：外＝点線（Sの先端）、内＝実線。
            DrawDashedCircle(p, c, GuideOuter * _s, GuideOuterCol);
            p.BeginPath();
            p.Arc(c, GuideInner * _s, Deg(0f), Deg(360f));
            p.strokeColor = GuideInnerCol;
            p.lineWidth = 1f;
            p.Stroke();

            // 丸の列：同じ大きさ・等間隔・内側ほど薄い（位置は小数のまま）。
            var r = DotRadius();
            foreach (var pt in _pitches)
            {
                var u = Dir(pt.AngleDeg);
                var n = DotCount(pt.Kire);
                var col = PitchTypeStyle.DotColor(pt.Type);
                for (var i = 0; i < n; i++)
                {
                    var pos = c + u * ((FirstDot + DotStep * i) * _s);
                    p.BeginPath();
                    p.Arc(pos, r, Deg(0f), Deg(360f));
                    p.fillColor = new Color(col.r, col.g, col.b, 0.4f + 0.6f * (i + 1) / n);
                    p.Fill();
                }
            }

            // ボールの影（下に1px・ぼかし3px 相当を薄い同心円で近似）。ボール本体は子要素の画像。
            var br = Mathf.Max(16f, BallSize * _s) * 0.5f;
            var sc = c + new Vector2(0f, 1f);
            for (var k = 3; k >= 1; k--)
            {
                p.BeginPath();
                p.Arc(sc, br + k * 0.9f, Deg(0f), Deg(360f));
                p.fillColor = new Color(ShadowCol.r, ShadowCol.g, ShadowCol.b, 0.07f);
                p.Fill();
            }
        }

        // 点線の円：短い弧を等間隔に並べる（破線 3px・間 3px）。
        private static void DrawDashedCircle(Painter2D p, Vector2 c, float radius, Color col)
        {
            var circ = 2f * Mathf.PI * radius;
            var count = Mathf.Max(12, Mathf.RoundToInt(circ / 6f));
            var step = 360f / count;
            p.strokeColor = col;
            p.lineWidth = 1f;
            for (var i = 0; i < count; i++)
            {
                var a0 = i * step;
                p.BeginPath();
                p.Arc(c, radius, Deg(a0), Deg(a0 + step * 0.5f));
                p.Stroke();
            }
        }

        private static Angle Deg(float d) => new Angle(d, AngleUnit.Degree);
    }
}

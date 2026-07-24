using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Shell;   // RankPalette（キレ等級色のコード側単一ソース）

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 球種変化チャート1枚ぶんの入力（プロスピ風の扇形1枚＝1球種）。
    /// 部品辞書の <see cref="PitchChartView"/> が読む共通データ型（issue #94: 画面ローカル実装からの昇格）。
    /// </summary>
    public sealed class PitchChartDatum
    {
        public string Name = "";
        public string Kire = "C";     // キレ（Sharpness→S〜G）＝扇の色とチップ
        public float DirX;            // 変化方向（画面座標系: +x右 / +y下）
        public float DirY;
        public float Break01;         // 変化量 0〜1（扇の長さ）
        public bool IsFastball;       // ストレートは変化量でなく「伸び」で読ませる（短尺固定＋幅で表現）
        public float Extend01;        // 伸び 0〜1（ストレートのみ意味を持つ＝扇の幅）
        public string VeloText = "";  // ストレートに添える最速（"148"。null/空で出さない）
    }

    /// <summary>
    /// 球種変化チャートの描画部品（部品辞書・UI原則⑤）。プロスピ風の扇形セクター塗り
    /// （扇の長さ＝変化量 / 幅＝固定・ストレートのみ伸びで可変 / 色＝キレ等級 RankPalette）＋中心のボール。
    /// 選手詳細で確立した見た目を1箇所へ集約し、比較UI（メンバー/スタメン設定）のカードからも同じ絵を出せるようにする。
    /// <paramref name="compact"/>＝比較カード埋め込み用の小型版（ボール半径・最速添えを縮小）。
    /// </summary>
    public sealed class PitchChartView
    {
        private readonly VisualElement _host;
        private readonly bool _compact;
        private readonly float _ballR;
        private readonly float _arcInner;

        private readonly List<PitchChartDatum> _pitches = new List<PitchChartDatum>();
        private readonly List<PitchArc> _arcs = new List<PitchArc>();
        private Vector2 _center;
        private Vector2 _scale = Vector2.one;

        // 扇1枚分の描画パラメータ（Layout で確定し OnPaint が読む）。
        private struct PitchArc
        {
            public float AngleDeg;    // 変化方向（画面座標系・0度=右／時計回り）
            public float HalfDeg;     // 扇の半幅
            public float Outer;       // 外半径（＝変化量）
            public Color Col;         // キレ等級色
        }

        public PitchChartView(VisualElement host, bool compact = false)
        {
            _host = host;
            _compact = compact;
            _ballR = compact ? 12f : 22f;
            _arcInner = _ballR + 4f;
            if (_host == null) return;
            _host.generateVisualContent += OnPaint;
            _host.RegisterCallback<GeometryChangedEvent>(_ => Layout());
        }

        /// <summary>表示する持ち球を差し替える（空/nullでチャートを隠す）。</summary>
        public void SetData(IReadOnlyList<PitchChartDatum> pitches)
        {
            _host?.Clear();
            _arcs.Clear();
            _pitches.Clear();
            var has = pitches != null && pitches.Count > 0;
            if (_host != null) _host.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (!has) { _host?.MarkDirtyRepaint(); return; }

            _pitches.AddRange(pitches);
            foreach (var pt in _pitches)
            {
                var chip = new VisualElement();
                chip.AddToClassList("pd2-pchip");
                chip.userData = pt;
                var name = new Label(pt.Name); name.AddToClassList("pd2-pchip__name"); chip.Add(name);
                // ストレートのみ最速を添える（小型版は省スペースのため出さない）。
                if (pt.IsFastball && !_compact && !string.IsNullOrEmpty(pt.VeloText))
                    chip.Add(UiComponents.NumUnit(pt.VeloText, "km/h", false, "pd2-pchip__velo"));
                chip.Add(UiComponents.RankChip(pt.Kire));
                // 差し替え直後は resolvedStyle が未確定（幅が既定値扱い）のため、チップの実寸が
                // 確定したタイミングで再レイアウトする。ホスト寸法が変わらない再描画（行ホバーでの
                // 選手切替）では host の GeometryChangedEvent が来ないので、チップ側で拾うのが確実。
                chip.RegisterCallback<GeometryChangedEvent>(_ => Layout());
                _host.Add(chip);
            }
            Layout();
            _host.MarkDirtyRepaint();
        }

        // 中心・半径から扇の寸法を決め、各ラベルを扇の先端の外側へ置く。
        private void Layout()
        {
            if (_host == null) return;
            var rect = _host.contentRect;
            if (rect.width < 40f || rect.height < 40f) return;

            _center = new Vector2(rect.width * 0.5f, rect.height * 0.5f);
            var radius = Mathf.Min(rect.width, rect.height) * 0.44f;
            _scale = new Vector2(Mathf.Clamp(rect.width / Mathf.Max(1f, rect.height) * 0.5f, 1f, 2.2f), 1f);
            _arcs.Clear();
            var chips = new List<VisualElement>();
            var boxes = new List<Rect>();

            foreach (var child in _host.Children())
            {
                var pt = child.userData as PitchChartDatum;
                if (pt == null) continue;

                var dir = new Vector2(pt.DirX, pt.DirY);
                if (dir.sqrMagnitude < 0.0001f) dir = new Vector2(0f, -1f);
                dir = dir.normalized;

                // 扇の長さ＝変化量。ストレートは Break01 が短尺固定なので自然に短い扇になる。
                var outer = Mathf.Max(_arcInner + 10f, Mathf.Lerp(radius * 0.26f, radius * 0.92f, Mathf.Clamp01(pt.Break01)));
                // 幅は固定。ストレートだけ「伸び」を幅で表す（長さではなく太さで読ませる）。
                var half = pt.IsFastball ? Mathf.Lerp(13f, 26f, Mathf.Clamp01(pt.Extend01)) : 15f;

                var angleDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                _arcs.Add(new PitchArc { AngleDeg = angleDeg, HalfDeg = half, Outer = outer, Col = RankPalette.Of(pt.Kire) });

                var lw = child.resolvedStyle.width;
                var lh = child.resolvedStyle.height;
                if (lw < 1f) lw = 96f;
                if (lh < 1f) lh = 28f;
                // ラベルは扇の先端の「外側」へ逃がす（隣り合う球種どうしの重なりも方向ごとに外へ押し出して減らす）。
                var tip = PolarPoint(_center, outer + 12f, angleDeg);
                var end = tip + new Vector2(dir.x * lw * 0.5f, dir.y * lh * 0.5f);
                chips.Add(child);
                boxes.Add(new Rect(end.x - lw * 0.5f, end.y - lh * 0.5f, lw, lh));
            }

            SeparateChips(boxes, rect);
            for (var i = 0; i < chips.Count; i++)
            {
                chips[i].style.left = boxes[i].x;
                chips[i].style.top = boxes[i].y;
            }
            _host.MarkDirtyRepaint();
        }

        // ラベルの重なりを解く（変化方向が近い球種同士＝落ち球が重なりやすい）。侵入量の小さい軸へ押し分けるAABBリラクゼーション。
        private static void SeparateChips(List<Rect> boxes, Rect rect)
        {
            const float Gap = 4f;
            for (var pass = 0; pass < 12; pass++)
            {
                var moved = false;
                for (var i = 0; i < boxes.Count; i++)
                for (var j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i];
                    var b = boxes[j];
                    var dx = a.center.x - b.center.x;
                    var dy = a.center.y - b.center.y;
                    var ox = (a.width + b.width) * 0.5f + Gap - Mathf.Abs(dx);
                    var oy = (a.height + b.height) * 0.5f + Gap - Mathf.Abs(dy);
                    if (ox <= 0f || oy <= 0f) continue;

                    if (oy <= ox)
                    {
                        var s = (dy >= 0f ? 1f : -1f) * oy * 0.5f;
                        a.y += s; b.y -= s;
                    }
                    else
                    {
                        var s = (dx >= 0f ? 1f : -1f) * ox * 0.5f;
                        a.x += s; b.x -= s;
                    }
                    boxes[i] = a; boxes[j] = b;
                    moved = true;
                }
                if (!moved) break;
            }

            for (var i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                b.x = Mathf.Clamp(b.x, 0f, Mathf.Max(0f, rect.width - b.width));
                b.y = Mathf.Clamp(b.y, 0f, Mathf.Max(0f, rect.height - b.height));
                boxes[i] = b;
            }
        }

        private void OnPaint(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            var c = _center;
            if (c.x < 1f && c.y < 1f) return;

            // 各球種の扇（塗り＝キレ等級色の半透明、先端に同色の帯で変化量の端を明示）。
            // 横方向へ引き伸ばすため Painter2D.Arc は使わず、円弧を等分サンプルした多角形で描く。
            const int Seg = 14;
            foreach (var a in _arcs)
            {
                var from = a.AngleDeg - a.HalfDeg;
                var to = a.AngleDeg + a.HalfDeg;

                p.BeginPath();
                p.MoveTo(PolarPoint(c, _arcInner, from));
                for (var i = 1; i <= Seg; i++) p.LineTo(PolarPoint(c, _arcInner, Mathf.Lerp(from, to, i / (float)Seg)));
                for (var i = Seg; i >= 0; i--) p.LineTo(PolarPoint(c, a.Outer, Mathf.Lerp(from, to, i / (float)Seg)));
                p.ClosePath();
                p.fillColor = new Color(a.Col.r, a.Col.g, a.Col.b, 0.26f);
                p.Fill();

                p.BeginPath();
                p.MoveTo(PolarPoint(c, a.Outer, from));
                for (var i = 1; i <= Seg; i++) p.LineTo(PolarPoint(c, a.Outer, Mathf.Lerp(from, to, i / (float)Seg)));
                p.strokeColor = new Color(a.Col.r, a.Col.g, a.Col.b, 0.90f);
                p.lineWidth = 2.5f;
                p.Stroke();
            }

            DrawBall(p, c, _ballR);
        }

        // 中心のボール（ベジェ縫い目＋ステッチ、淡いハイライト、薄い影）。
        private static void DrawBall(Painter2D p, Vector2 c, float ballR)
        {
            p.BeginPath();
            p.Arc(c + new Vector2(0f, 2.5f), ballR * 1.04f, Deg(0f), Deg(360f));
            p.fillColor = new Color(0f, 0f, 0f, 0.30f); p.Fill();

            p.BeginPath();
            p.Arc(c, ballR, Deg(0f), Deg(360f));
            p.fillColor = new Color(0.941f, 0.957f, 0.918f); p.Fill();

            p.BeginPath();
            p.Arc(c + new Vector2(-ballR * 0.30f, -ballR * 0.32f), ballR * 0.46f, Deg(0f), Deg(360f));
            p.fillColor = new Color(1f, 1f, 1f, 0.55f); p.Fill();

            p.BeginPath();
            p.Arc(c, ballR, Deg(0f), Deg(360f));
            p.strokeColor = new Color(0.30f, 0.35f, 0.30f); p.lineWidth = 1f; p.Stroke();

            var red = new Color(0.85f, 0.30f, 0.22f);
            DrawSeam(p, c, -1f, red, ballR);
            DrawSeam(p, c, 1f, red, ballR);
        }

        // side = -1 で左の縫い目、+1 で右の縫い目（中心へ向かって弓なりに反る）。
        private static void DrawSeam(Painter2D p, Vector2 c, float side, Color red, float ballR)
        {
            var p0 = c + new Vector2(side * ballR * 0.66f, -ballR * 0.80f);
            var c1 = c + new Vector2(-side * ballR * 0.24f, -ballR * 0.44f);
            var c2 = c + new Vector2(-side * ballR * 0.24f, ballR * 0.44f);
            var p3 = c + new Vector2(side * ballR * 0.66f, ballR * 0.80f);

            p.BeginPath();
            p.MoveTo(p0);
            p.BezierCurveTo(c1, c2, p3);
            p.strokeColor = red; p.lineWidth = 1.4f; p.Stroke();

            for (var i = 0; i < 6; i++)
            {
                var t = 0.10f + 0.16f * i;
                var pos = Cubic(p0, c1, c2, p3, t);
                var tan = Cubic(p0, c1, c2, p3, t + 0.02f) - Cubic(p0, c1, c2, p3, t - 0.02f);
                if (tan.sqrMagnitude < 0.0001f) continue;
                var n = new Vector2(-tan.y, tan.x).normalized * (ballR * 0.20f);
                p.BeginPath();
                p.MoveTo(pos - n);
                p.LineTo(pos + n);
                p.strokeColor = red; p.lineWidth = 1.1f; p.Stroke();
            }
        }

        private static Vector2 Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            var u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        // 内半径ぶんは等方（ボール縁に必ず接する）、その外側だけ横へ引き伸ばす。
        private Vector2 PolarPoint(Vector2 c, float r, float deg)
        {
            var rad = deg * Mathf.Deg2Rad;
            var u = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            return c + u * _arcInner + Scale(u * Mathf.Max(0f, r - _arcInner));
        }

        private Vector2 Scale(Vector2 v) => new Vector2(v.x * _scale.x, v.y * _scale.y);

        private static Angle Deg(float d) => new Angle(d, AngleUnit.Degree);
    }
}

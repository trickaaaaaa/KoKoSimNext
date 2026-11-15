using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>折れ線1本ぶんのデータ（成長推移・設計書06 §3.2・issue #1027）。</summary>
    public sealed class GrowthSeries
    {
        /// <summary>凡例の名前（例「ミート」）。</summary>
        public string Label = "";
        /// <summary>凡例の右端に出す今の値（例「62」「136km/h」）。</summary>
        public string ValueText = "";
        /// <summary>線の色。<c>gchart__sw--*</c> の修飾子名（例 "batting"）＝tokens.uss の練習メニュー色 <c>--tm-*</c>。</summary>
        public string ColorKey = "";
        /// <summary>点列。x＝3年間のうちの位置（0〜1）、y＝能力レベル。</summary>
        public List<Vector2> Points = new List<Vector2>();
    }

    /// <summary>
    /// 成長推移の折れ線（部品辞書・UI原則⑤・issue #1027）。横軸は3年間で固定（1年4月〜3年の年度末）＝
    /// 線が今どこまで来ているかで在学の進み具合も読める。縦軸は表示中の線の範囲に合わせて10刻みで張る
    /// （0〜100固定だと伸びが寝てしまう）。
    ///
    /// <para>色は C# に持たない。凡例の色見本（<c>.gchart__sw--*</c>＝tokens.uss の <c>--tm-*</c>）の
    /// 解決済み背景色をそのまま線に使う＝トークンの外に出ない。グリッド線は描画面の <c>color</c>。</para>
    /// </summary>
    public sealed class GrowthChartView
    {
        private const float LineWidth = 2f;
        private const float DotRadius = 3.5f;

        private readonly VisualElement _plot;
        private readonly VisualElement _yAxis;
        private readonly VisualElement _legend;
        private readonly List<GrowthSeries> _series = new List<GrowthSeries>();
        private readonly List<VisualElement> _swatches = new List<VisualElement>();
        private readonly List<Label> _yLabels = new List<Label>();
        private float _lo = 0f, _hi = 100f, _step = 20f;

        /// <param name="host">器（UXML の name 付き VisualElement）。中身はこの部品が組む。</param>
        /// <param name="xLabels">横軸の区切り（等分。成長推移では「1年」「2年」「3年」）。</param>
        public GrowthChartView(VisualElement host, IReadOnlyList<string> xLabels)
        {
            if (host == null) return;
            host.Clear();
            host.AddToClassList("gchart");

            var main = new VisualElement();
            main.AddToClassList("gchart__main");
            host.Add(main);

            var body = new VisualElement();
            body.AddToClassList("gchart__body");
            main.Add(body);

            _yAxis = new VisualElement();
            _yAxis.AddToClassList("gchart__yaxis");
            body.Add(_yAxis);

            _plot = new VisualElement();
            _plot.AddToClassList("gchart__plot");
            _plot.generateVisualContent += OnPaint;
            _plot.RegisterCallback<GeometryChangedEvent>(_ => { _plot.MarkDirtyRepaint(); PlaceYLabels(); });
            body.Add(_plot);

            var xAxis = new VisualElement();
            xAxis.AddToClassList("gchart__xaxis");
            foreach (var t in xLabels)
            {
                var l = new Label(t);
                l.AddToClassList("gchart__x");
                l.AddToClassList("f-body");
                xAxis.Add(l);
            }
            main.Add(xAxis);

            _legend = new VisualElement();
            _legend.AddToClassList("gchart__legend");
            host.Add(_legend);
        }

        /// <summary>表示する線を差し替える。</summary>
        public void SetData(IReadOnlyList<GrowthSeries> series)
        {
            if (_plot == null) return;
            _series.Clear();
            if (series != null) _series.AddRange(series);
            ComputeRange();
            BuildLegend();
            BuildYLabels();
            PlaceYLabels();
            _plot.MarkDirtyRepaint();
        }

        private void ComputeRange()
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var s in _series)
                foreach (var p in s.Points) { min = Mathf.Min(min, p.y); max = Mathf.Max(max, p.y); }
            if (min > max) { min = 0f; max = 100f; }

            // 10刻みに広げ、線が枠に貼り付かないよう上下に余白を取る。最低でも20の幅を持たせる。
            _lo = Mathf.Max(0f, Mathf.Floor((min - 5f) / 10f) * 10f);
            _hi = Mathf.Min(100f, Mathf.Ceil((max + 5f) / 10f) * 10f);
            if (_hi - _lo < 20f)
            {
                if (_hi + 10f <= 100f) _hi += 10f; else _lo -= 10f;
                if (_hi - _lo < 20f) { if (_lo - 10f >= 0f) _lo -= 10f; else _hi += 10f; }
            }
            _step = _hi - _lo > 40f ? 20f : 10f;
            // 20刻みなら上端も刻みに揃える（上端に目盛りの数字が無い段を作らない）。
            if (_step > 10f)
            {
                _hi = _lo + Mathf.Ceil((_hi - _lo) / _step) * _step;
                if (_hi > 100f) { _lo -= _hi - 100f; _hi = 100f; }
            }
        }

        private void BuildLegend()
        {
            _legend.Clear();
            _swatches.Clear();
            foreach (var s in _series)
            {
                var row = new VisualElement();
                row.AddToClassList("gchart__lg");

                var sw = new VisualElement();
                sw.AddToClassList("gchart__sw");
                if (!string.IsNullOrEmpty(s.ColorKey)) sw.AddToClassList("gchart__sw--" + s.ColorKey);
                // 線の色はここから読む＝スタイルが解決したら描き直す。
                sw.RegisterCallback<GeometryChangedEvent>(_ => _plot.MarkDirtyRepaint());
                row.Add(sw);
                _swatches.Add(sw);

                var name = new Label(s.Label);
                name.AddToClassList("gchart__lg-name");
                name.AddToClassList("f-body");
                row.Add(name);

                var val = new Label(s.ValueText);
                val.AddToClassList("gchart__lg-val");
                val.AddToClassList("f-num");
                row.Add(val);

                _legend.Add(row);
            }
        }

        private void BuildYLabels()
        {
            foreach (var l in _yLabels) l.RemoveFromHierarchy();
            _yLabels.Clear();
            for (var v = _lo; v <= _hi + 0.01f; v += _step)
            {
                var l = new Label(((int)v).ToString());
                l.AddToClassList("gchart__y");
                l.AddToClassList("f-num");
                l.userData = v;
                _yAxis.Add(l);
                _yLabels.Add(l);
            }
        }

        private void PlaceYLabels()
        {
            var h = _plot.contentRect.height;
            if (float.IsNaN(h) || h < 4) return;
            foreach (var l in _yLabels)
            {
                var y = YOf((float)l.userData, h);
                l.style.top = y;
                l.style.translate = new Translate(0, Length.Percent(-50));
            }
        }

        private float YOf(float v, float height) => height * (1f - (v - _lo) / Mathf.Max(1f, _hi - _lo));

        private void OnPaint(MeshGenerationContext ctx)
        {
            var rect = _plot.contentRect;
            if (rect.width < 4 || rect.height < 4) return;
            var p = ctx.painter2D;

            // グリッド（縦軸の目盛りと同じ位置）。
            p.strokeColor = _plot.resolvedStyle.color;
            p.lineWidth = 1f;
            for (var v = _lo; v <= _hi + 0.01f; v += _step)
            {
                var y = YOf(v, rect.height);
                p.BeginPath();
                p.MoveTo(new Vector2(0, y));
                p.LineTo(new Vector2(rect.width, y));
                p.Stroke();
            }
            // 学年の区切り（横軸を3等分）。
            for (var i = 1; i < 3; i++)
            {
                var x = rect.width * i / 3f;
                p.BeginPath();
                p.MoveTo(new Vector2(x, 0));
                p.LineTo(new Vector2(x, rect.height));
                p.Stroke();
            }

            for (var i = 0; i < _series.Count; i++)
            {
                var s = _series[i];
                if (s.Points.Count == 0) continue;
                var color = i < _swatches.Count ? _swatches[i].resolvedStyle.backgroundColor : Color.black;

                p.strokeColor = color;
                p.lineWidth = LineWidth;
                p.lineJoin = LineJoin.Round;
                p.lineCap = LineCap.Round;
                if (s.Points.Count >= 2)
                {
                    p.BeginPath();
                    for (var k = 0; k < s.Points.Count; k++)
                    {
                        var pt = ToPx(s.Points[k], rect);
                        if (k == 0) p.MoveTo(pt); else p.LineTo(pt);
                    }
                    p.Stroke();
                }

                // 今の値（最後の点）に点を打つ。
                var last = ToPx(s.Points[s.Points.Count - 1], rect);
                p.fillColor = color;
                p.BeginPath();
                p.Arc(last, DotRadius, 0f, 360f);
                p.Fill();
            }
        }

        private Vector2 ToPx(Vector2 v, Rect rect)
            => new Vector2(Mathf.Clamp01(v.x) * rect.width, YOf(Mathf.Clamp(v.y, _lo, _hi), rect.height));
    }
}

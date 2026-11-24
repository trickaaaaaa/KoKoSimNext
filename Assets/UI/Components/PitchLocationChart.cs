using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Unity.Components
{
    /// <summary>投球チャートの印1つぶん（その打席の n 球目）。コースは本塁面 [m]（X=+一塁側・Y=高さ）。</summary>
    public readonly struct PitchLocationMark
    {
        public readonly int Number;
        public readonly float X, Y;
        public readonly PitchKind Kind;
        public readonly PitchType? Type;
        public readonly Handedness Throws;

        public PitchLocationMark(int number, float x, float y, PitchKind kind, PitchType? type, Handedness throws)
        {
            Number = number; X = x; Y = y; Kind = kind; Type = type; Throws = throws;
        }
    }

    /// <summary>
    /// 投球チャートの座標変換と印の見た目の決め（単一ソース）。図は<b>投手の後ろから見た向き</b>
    /// （対決ビューのカメラと同じ）＝本塁面の +X（一塁側）は図の<b>左</b>、右打者（三塁側＝-X に立つ）は図の右。
    /// 図の範囲はゾーンの外へ半幅ぶんの余白を足した矩形で、外れ球もこの中に収まる。
    /// </summary>
    public static class PitchLocationMap
    {
        /// <summary>図が映す範囲[m]（ゾーン＋四方に半幅ぶん）。</summary>
        public static void Extent(StrikeZone zone, out float halfW, out float bottom, out float top)
        {
            var pad = (float)zone.HalfWidthM;
            halfW = (float)zone.HalfWidthM + pad;
            bottom = (float)zone.BottomM - pad;
            top = (float)zone.TopM + pad;
        }

        /// <summary>1mあたりの画素数。図の枠に範囲が収まる最大の等倍率（縦横比は歪めない）。</summary>
        public static float Scale(StrikeZone zone, float width, float height)
        {
            Extent(zone, out var halfW, out var bottom, out var top);
            return Mathf.Min(width / (halfW * 2f), height / (top - bottom));
        }

        /// <summary>本塁面 (x, y)[m] → 図の画素（左上原点・下向きが +y）。範囲の外はそのまま外へ出る（呼び出し側で寄せる）。</summary>
        public static Vector2 ToChart(StrikeZone zone, float x, float y, float width, float height)
        {
            Extent(zone, out _, out var bottom, out var top);
            var s = Scale(zone, width, height);
            var cx = width * 0.5f;
            var cy = height * 0.5f;
            var midY = (top + bottom) * 0.5f;
            return new Vector2(cx - x * s, cy - (y - midY) * s);
        }

        /// <summary>印の色＝結果。赤＝ストライク（見逃し・空振り）／緑＝ボール・死球／黄＝ファウル／青＝インプレー。</summary>
        public static Color ColorOf(PitchKind kind)
        {
            switch (kind)
            {
                case PitchKind.CalledStrike:
                case PitchKind.SwingingStrike: return new Color32(0xE8, 0x45, 0x2E, 0xFF);
                case PitchKind.Foul: return new Color32(0xF5, 0xC6, 0x4A, 0xFF);
                case PitchKind.InPlay: return new Color32(0x3F, 0x86, 0xE0, 0xFF);
                default: return new Color32(0x3F, 0xB3, 0x6B, 0xFF);   // Ball / HitByPitch
            }
        }

        /// <summary>印の中の数字の色。黄だけ地が明るいので墨にする。</summary>
        public static Color NumberColorOf(PitchKind kind)
            => kind == PitchKind.Foul ? new Color32(0x1D, 0x1B, 0x18, 0xFF) : Color.white;

        /// <summary>三角にするか。ストレート（<see cref="PitchType.Fastball"/>）は丸、それ以外の球種は三角。</summary>
        public static bool IsTriangle(PitchType? type) => type.HasValue && type.Value != PitchType.Fastball;

        /// <summary>三角の頂点を向ける画面上の単位ベクトル（球種チャートと同じ <see cref="PitchTypeStyle.AngleFor"/>）。</summary>
        public static Vector2 ApexDirection(PitchType type, Handedness throws)
        {
            var a = PitchTypeStyle.AngleFor(type, throws) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(a), -Mathf.Sin(a));
        }
    }

    /// <summary>
    /// 投球チャート（対決ビューの右の中ほど）。ゾーンの枠＋9分割の線の上に、その打席で投げた球のコースを
    /// 結果の色・球種の形・投球番号の印で打っていく。打者が替わったら <see cref="ClearMarks"/>。
    /// </summary>
    public sealed class PitchLocationChart : VisualElement
    {
        public const float ChartWidth = 180f;
        public const float ChartHeight = 204f;
        private const float CircleR = 11f;
        private const float TriangleR = 14f;

        private static readonly Color FrameCol = new Color(1f, 1f, 1f, 0.9f);
        private static readonly Color GridCol = new Color(1f, 1f, 1f, 0.45f);
        private static readonly Color MarkEdgeCol = new Color(0f, 0f, 0f, 0.6f);

        private readonly List<PitchLocationMark> _marks = new List<PitchLocationMark>();
        private StrikeZone _zone = new StrikeZone();

        public int Count => _marks.Count;

        public PitchLocationChart()
        {
            AddToClassList("plc");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += OnPaint;
        }

        public void SetZone(StrikeZone zone)
        {
            _zone = zone ?? new StrikeZone();
            Rebuild();
        }

        public void AddMark(PitchLocationMark mark)
        {
            _marks.Add(mark);
            Rebuild();
        }

        public void ClearMarks()
        {
            if (_marks.Count == 0) return;
            _marks.Clear();
            Rebuild();
        }

        public Vector2 PointOf(PitchLocationMark m)
        {
            var p = PitchLocationMap.ToChart(_zone, m.X, m.Y, ChartWidth, ChartHeight);
            var r = RadiusOf(m);
            return new Vector2(Mathf.Clamp(p.x, r, ChartWidth - r), Mathf.Clamp(p.y, r, ChartHeight - r));
        }

        private static float RadiusOf(PitchLocationMark m) => PitchLocationMap.IsTriangle(m.Type) ? TriangleR : CircleR;

        private void Rebuild()
        {
            // 数字は印ごとの Label（painter2D は文字を描けない）。印の描画と同じ位置に重ねる。
            Clear();
            foreach (var m in _marks)
            {
                var p = PointOf(m);
                var r = RadiusOf(m);
                var lab = new Label(m.Number.ToString()) { pickingMode = PickingMode.Ignore };
                lab.AddToClassList("plc__num");
                lab.AddToClassList("f-num-bd");
                lab.style.left = p.x - r;
                lab.style.top = p.y - r;
                lab.style.width = r * 2f;
                lab.style.height = r * 2f;
                lab.style.color = PitchLocationMap.NumberColorOf(m.Kind);
                Add(lab);
            }
            MarkDirtyRepaint();
        }

        private void OnPaint(MeshGenerationContext ctx)
        {
            var p = ctx.painter2D;
            DrawZone(p);
            foreach (var m in _marks) DrawMark(p, m);
        }

        private void DrawZone(Painter2D p)
        {
            var tl = PitchLocationMap.ToChart(_zone, (float)_zone.HalfWidthM, (float)_zone.TopM, ChartWidth, ChartHeight);
            var br = PitchLocationMap.ToChart(_zone, -(float)_zone.HalfWidthM, (float)_zone.BottomM, ChartWidth, ChartHeight);

            p.strokeColor = GridCol;
            p.lineWidth = 1f;
            for (var i = 1; i <= 2; i++)
            {
                var x = Mathf.Lerp(tl.x, br.x, i / 3f);
                var y = Mathf.Lerp(tl.y, br.y, i / 3f);
                p.BeginPath();
                p.MoveTo(new Vector2(x, tl.y));
                p.LineTo(new Vector2(x, br.y));
                p.Stroke();
                p.BeginPath();
                p.MoveTo(new Vector2(tl.x, y));
                p.LineTo(new Vector2(br.x, y));
                p.Stroke();
            }

            p.strokeColor = FrameCol;
            p.lineWidth = 2f;
            p.BeginPath();
            p.MoveTo(tl);
            p.LineTo(new Vector2(br.x, tl.y));
            p.LineTo(br);
            p.LineTo(new Vector2(tl.x, br.y));
            p.ClosePath();
            p.Stroke();
        }

        private void DrawMark(Painter2D p, PitchLocationMark m)
        {
            var c = PointOf(m);
            p.fillColor = PitchLocationMap.ColorOf(m.Kind);
            p.strokeColor = MarkEdgeCol;
            p.lineWidth = 1.5f;
            p.BeginPath();
            if (PitchLocationMap.IsTriangle(m.Type))
            {
                var d = PitchLocationMap.ApexDirection(m.Type.Value, m.Throws);
                var a = Mathf.Atan2(d.y, d.x);
                for (var k = 0; k < 3; k++)
                {
                    var ang = a + k * (2f * Mathf.PI / 3f);
                    var v = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * TriangleR;
                    if (k == 0) p.MoveTo(v); else p.LineTo(v);
                }
                p.ClosePath();
            }
            else
            {
                p.Arc(c, CircleR, new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
            }
            p.Fill();
            p.Stroke();
        }
    }
}

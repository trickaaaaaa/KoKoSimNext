using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Players;   // RadarAxis（軸データ）
using KokoSim.Unity.Shell;     // RankPalette（ランク色のコード側単一ソース）

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// レーダー（N角形）の描画部品（部品辞書・UI原則⑤）。チーム総合力パネルで確立した見た目を
    /// 1箇所に集約し、練習試合の相手校レーダーなど他画面から同じ絵を出せるようにする。
    /// 5段階グリッド＋スポーク、中心→外のグラデ塗り（総合ランク連動色）、輪郭線と頂点ドット。
    /// 軸ラベルは要素の重なり順の都合で呼び出し側が VisualElement として配置する
    /// （<see cref="AxisPoint"/> と同じ角度計算を使えば位置が一致する）。
    /// </summary>
    public static class RadarChart
    {
        /// <summary>グリッドの段数（20/40/60/80/100）。</summary>
        private const int Rings = 5;

        /// <summary>
        /// レーダーを描く。<paramref name="axes"/> は真上から時計回りに並べる。
        /// <paramref name="grade"/> は塗り色を決めるランク（S〜G）。
        /// </summary>
        public static void Paint(MeshGenerationContext ctx, IReadOnlyList<RadarAxis> axes,
            string grade, float radiusFactor)
        {
            var n = axes.Count;
            if (n < 3) return;
            var rect = ctx.visualElement.contentRect;
            if (rect.width < 4 || rect.height < 4) return;

            var cx = rect.width * 0.5f;
            var cy = rect.height * 0.5f;
            var radius = Mathf.Min(rect.width, rect.height) * radiusFactor;
            var p = ctx.painter2D;

            // グリッド: 最外周だけ明るく、内側は暗く細く。
            var gridInner = new Color(0.17f, 0.24f, 0.20f);
            var gridOuter = new Color(0.40f, 0.52f, 0.45f);
            for (var r = 1; r <= Rings; r++)
            {
                var isOuter = r == Rings;
                p.BeginPath();
                for (var i = 0; i < n; i++)
                {
                    var pt = AxisPoint(cx, cy, radius * ((float)r / Rings), i, n);
                    if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                }
                p.ClosePath();
                p.strokeColor = isOuter ? gridOuter : gridInner;
                p.lineWidth = isOuter ? 1.6f : 1f;
                p.Stroke();
            }
            for (var i = 0; i < n; i++)
            {
                p.BeginPath();
                p.MoveTo(new Vector2(cx, cy));
                p.LineTo(AxisPoint(cx, cy, radius, i, n));
                p.strokeColor = gridInner;
                p.lineWidth = 1f;
                p.Stroke();
            }

            // データ塗り: ランク連動色で中心→外へグラデ（黄アクセントは使わない）。
            var rc = RankPalette.Of(grade);
            var centerCol = (Color32)new Color(rc.r, rc.g, rc.b, 0.28f);
            var rimCol = (Color32)new Color(rc.r * 0.68f, rc.g * 0.68f, rc.b * 0.68f, 0.62f);

            var rim = new Vector2[n];
            for (var i = 0; i < n; i++)
            {
                var val = Mathf.Clamp01(axes[i].Value01);
                rim[i] = AxisPoint(cx, cy, radius * Mathf.Max(0.04f, val), i, n);
            }
            var mesh = ctx.Allocate(n + 1, n * 3);
            mesh.SetNextVertex(new Vertex { position = new Vector3(cx, cy, Vertex.nearZ), tint = centerCol });
            for (var i = 0; i < n; i++)
                mesh.SetNextVertex(new Vertex { position = new Vector3(rim[i].x, rim[i].y, Vertex.nearZ), tint = rimCol });
            for (var i = 0; i < n; i++)
            {
                mesh.SetNextIndex(0);
                mesh.SetNextIndex((ushort)(1 + i));
                mesh.SetNextIndex((ushort)(1 + ((i + 1) % n)));
            }

            // 輪郭線（2.5px）＋頂点ドット（4px）＝ランク連動色（濃いめ）。
            var line = new Color(rc.r, rc.g, rc.b, 1f);
            p.BeginPath();
            for (var i = 0; i < n; i++) { if (i == 0) p.MoveTo(rim[i]); else p.LineTo(rim[i]); }
            p.ClosePath();
            p.strokeColor = line;
            p.lineWidth = 2.5f;
            p.Stroke();

            foreach (var pt in rim)
            {
                p.BeginPath();
                p.MoveTo(new Vector2(pt.x - 2f, pt.y));
                p.LineTo(new Vector2(pt.x, pt.y - 2f));
                p.LineTo(new Vector2(pt.x + 2f, pt.y));
                p.LineTo(new Vector2(pt.x, pt.y + 2f));
                p.ClosePath();
                p.fillColor = line;
                p.Fill();
            }
        }

        /// <summary>i 番目の軸上、中心から距離 r の点（真上始点・時計回り）。軸ラベルの配置にも使う。</summary>
        public static Vector2 AxisPoint(float cx, float cy, float r, int i, int n)
        {
            var ang = -Mathf.PI / 2f + (Mathf.PI * 2f) * i / n;
            return new Vector2(cx + Mathf.Cos(ang) * r, cy + Mathf.Sin(ang) * r);
        }
    }
}

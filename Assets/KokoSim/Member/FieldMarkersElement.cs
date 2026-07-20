using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Member
{
    /// <summary>
    /// 守備位置の「立ち位置点（真実）」と、そこからチップ（ラベル）への引き出し線を描くオーバーレイ。
    /// 点は球場座標由来で不動、チップだけが重なり回避で左右にずれる。点＝真実・チップ＝ラベルを可視化する。
    /// 点とチップの位置は Controller が計算して <see cref="SetMarkers"/> で渡す（本要素は描くだけ）。
    /// </summary>
    public sealed class FieldMarkersElement : VisualElement
    {
        public struct Marker
        {
            public Vector2 Dot;         // 立ち位置[px]
            public Vector2 ChipAnchor;  // チップ下端中央[px]
            public bool Lead;           // 引き出し線を描くか（チップがずれている時）
        }

        private readonly List<Marker> _markers = new List<Marker>();
        private Color _dot = new Color(0.937f, 0.957f, 0.918f);      // #EFF4EA
        private Color _lead = new Color(0.937f, 0.957f, 0.918f, 0.5f);

        public FieldMarkersElement()
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0; style.top = 0; style.right = 0; style.bottom = 0;
            generateVisualContent += OnGenerate;
        }

        public void SetMarkers(IEnumerable<Marker> markers)
        {
            _markers.Clear();
            _markers.AddRange(markers);
            MarkDirtyRepaint();
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            // 先に引き出し線、次に点（点を前面に）。
            p.strokeColor = _lead;
            p.lineWidth = 1f;
            foreach (var m in _markers)
            {
                if (!m.Lead) continue;
                p.BeginPath();
                p.MoveTo(m.Dot);
                p.LineTo(m.ChipAnchor);
                p.Stroke();
            }
            p.fillColor = _dot;
            foreach (var m in _markers)
            {
                p.BeginPath();
                p.Arc(m.Dot, 2f, new Angle(0f, AngleUnit.Degree), new Angle(360f, AngleUnit.Degree));
                p.Fill();
            }
        }
    }
}

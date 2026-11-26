using System;
using KokoSim.Engine.Nation;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.NewGame
{
    /// <summary>
    /// 日本地図の部品（県選択画面・issue #1410）。engine の <see cref="MapView"/>（全国図／北海道・東京の拡大図）を
    /// Painter2D で塗り、乗せた区画を強調して名前を出し、クリックで決定する。
    ///
    /// <para><b>絵と当たり判定は同じ写像</b>（<see cref="ToMap"/> と <see cref="Transform"/>）を通る＝画面サイズが
    /// 変わってもずれない。点内判定は engine の <see cref="MapView.HitTest"/>（輪郭の中だけ・隣へ丸めない）。</para>
    /// </summary>
    public sealed class JapanMapElement : VisualElement
    {
        private const float Margin = 16f;   // 図の周りの余白[px]
        private const float LineWidth = 1.5f;

        // 海はカードの白のまま・陸は一段濃いベージュ（陸と海の差をはっきりさせる・issue #1502 配色2）
        private Color _land = Hex(0xD9D4C8);    // --sbk-card-line-hover
        private Color _landAlt = Hex(0xE6E2D8); // --sbk-seg-bg（拡大図の2つ目の区画＝一段薄く）
        private Color _line = Hex(0xFFFFFF);    // --sbk-card（区画の境界＝白い目地）
        private Color _hover = Hex(0x2F2C27);   // --sbk-ink（乗せた区画＝墨）

        private static readonly CustomStyleProperty<Color> LandProp = new("--sbk-card-line-hover");
        private static readonly CustomStyleProperty<Color> LandAltProp = new("--sbk-seg-bg");
        private static readonly CustomStyleProperty<Color> LineProp = new("--sbk-card");
        private static readonly CustomStyleProperty<Color> HoverProp = new("--sbk-ink");

        private readonly Label _tip = new Label();
        private MapView _view;
        private MapFeature _hover_;

        /// <summary>区画を決定した（クリック）。</summary>
        public event Action<MapFeature> Picked;

        /// <summary>いま乗せている区画（無ければ null）。</summary>
        public MapFeature Hovered => _hover_;

        /// <summary>区画の塗り色。null を返した区画は既定の陸の色（勢力図マップ・issue #1494）。</summary>
        public Func<MapFeature, Color?> FillOf { get; set; }

        /// <summary>名前の札の文言。null なら <see cref="LabelOf"/>（勢力図マップ・issue #1494）。</summary>
        public Func<MapFeature, string> TipOf { get; set; }

        public JapanMapElement()
        {
            AddToClassList("pmap");
            _tip.AddToClassList("pmap__tip");
            _tip.AddToClassList("f-display");
            _tip.pickingMode = PickingMode.Ignore;
            _tip.style.display = DisplayStyle.None;
            Add(_tip);

            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyles);
            RegisterCallback<GeometryChangedEvent>(_ => MarkDirtyRepaint());
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerLeaveEvent>(_ => SetHover(null, default));
            RegisterCallback<ClickEvent>(OnClick);
        }

        public MapView View => _view;

        public void SetView(MapView view)
        {
            _view = view;
            SetHover(null, default);
            MarkDirtyRepaint();
        }

        /// <summary>塗り色・札の文言の元データが変わった（描き直す）。</summary>
        public void Refresh() => MarkDirtyRepaint();

        /// <summary>要素のローカル座標 → 図の座標。</summary>
        public Vector2 ToMap(Vector2 local)
        {
            Transform(out var scale, out var origin);
            return (local - origin) / scale;
        }

        // 図全体を余白つきで要素の中央へ収める（縦横比は保つ）。
        private void Transform(out float scale, out Vector2 origin)
        {
            var r = contentRect;
            if (_view == null || _view.Width <= 0 || _view.Height <= 0 || r.width <= 0 || r.height <= 0)
            {
                scale = 1f; origin = Vector2.zero;
                return;
            }
            var aw = Mathf.Max(1f, r.width - Margin * 2f);
            var ah = Mathf.Max(1f, r.height - Margin * 2f);
            scale = Mathf.Min(aw / _view.Width, ah / _view.Height);
            origin = new Vector2((r.width - _view.Width * scale) * 0.5f, (r.height - _view.Height * scale) * 0.5f);
        }

        private void OnMove(PointerMoveEvent e)
        {
            if (_view == null) return;
            var p = ToMap(e.localPosition);
            SetHover(_view.HitTest(p.x, p.y), e.localPosition);
        }

        private void OnClick(ClickEvent e)
        {
            if (_view == null) return;
            var p = ToMap(e.localPosition);
            var hit = _view.HitTest(p.x, p.y);
            if (hit != null) Picked?.Invoke(hit);
        }

        private void SetHover(MapFeature f, Vector2 local)
        {
            if (f == null)
            {
                _tip.style.display = DisplayStyle.None;
            }
            else
            {
                _tip.text = TipOf?.Invoke(f) ?? LabelOf(f);
                _tip.style.display = DisplayStyle.Flex;
                // 名前の札はカーソルの右下。右端・下端で切れるなら反対側へ
                var flipX = local.x > contentRect.width - 140f;
                var flipY = local.y > contentRect.height - 60f;
                _tip.style.left = flipX ? local.x - 130f : local.x + 16f;
                _tip.style.top = flipY ? local.y - 44f : local.y + 18f;
            }
            if (_hover_ == f) return;
            _hover_ = f;
            MarkDirtyRepaint();
        }

#if UNITY_EDITOR
        /// <summary>【スクショ・検証用】区画の内側の点（外周の頂点の重心を、輪郭の中へ入るまで水平に探す）に乗せる。</summary>
        internal void HoverForCapture(int prefectureId, int? split)
        {
            foreach (var f in _view.Features)
            {
                if (f.PrefectureId != prefectureId || (split.HasValue && f.Split != split)) continue;
                var ring = f.Rings[0];
                for (var i = 0; i < f.Rings.Count; i++) if (f.Rings[i].Length > ring.Length) ring = f.Rings[i];
                double cx = 0, cy = 0;
                for (var i = 0; i < ring.Length; i += 2) { cx += ring[i]; cy += ring[i + 1]; }
                cx /= ring.Length / 2; cy /= ring.Length / 2;
                for (var d = 0; d < 400; d += 3)
                    foreach (var dx in new[] { d, -d })
                        if (f.Contains(cx + dx, cy))
                        {
                            Transform(out var scale, out var origin);
                            SetHover(f, new Vector2((float)(cx + dx), (float)cy) * scale + origin);
                            return;
                        }
            }
        }
#endif

        /// <summary>札の文字。拡大図は区画名（北北海道…）、全国図は県名。</summary>
        public static string LabelOf(MapFeature f)
            => f.Split.HasValue ? f.Name : PrefectureNames.JapaneseOf(f.PrefectureId);

        private void OnStyles(CustomStyleResolvedEvent e)
        {
            if (e.customStyle.TryGetValue(LandProp, out var c1)) _land = c1;
            if (e.customStyle.TryGetValue(LandAltProp, out var c2)) _landAlt = c2;
            if (e.customStyle.TryGetValue(LineProp, out var c3)) _line = c3;
            if (e.customStyle.TryGetValue(HoverProp, out var c4)) _hover = c4;
            MarkDirtyRepaint();
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            if (_view == null) return;
            Transform(out var scale, out var origin);
            var p2d = mgc.painter2D;

            // 乗せた区画は最後に描く（隣の目地の上に墨が載る）
            for (var pass = 0; pass < 2; pass++)
            foreach (var f in _view.Features)
            {
                var hovered = ReferenceEquals(f, _hover_);
                if (hovered != (pass == 1)) continue;
                var custom = FillOf?.Invoke(f);
                // 色で塗り分けた区画は、乗せても塗りを墨で潰さず枠だけ墨にする（何色かを読めなくしない）
                p2d.fillColor = custom.HasValue ? custom.Value : hovered ? _hover : (f.Split == 1 ? _landAlt : _land);
                p2d.strokeColor = hovered ? _hover : _line;
                p2d.lineWidth = hovered && custom.HasValue ? LineWidth * 2f : LineWidth;
                p2d.lineJoin = LineJoin.Round;
                p2d.BeginPath();
                foreach (var ring in f.Rings)
                {
                    p2d.MoveTo(Pt(ring, 0, scale, origin));
                    for (var i = 2; i < ring.Length; i += 2) p2d.LineTo(Pt(ring, i, scale, origin));
                    p2d.ClosePath();
                }
                p2d.Fill(FillRule.OddEven);
                p2d.Stroke();
            }
            // 沖縄の frame は当たり判定だけ（描かない・issue #1502）
        }

        private static Vector2 Pt(int[] ring, int i, float scale, Vector2 origin)
            => new Vector2(ring[i], ring[i + 1]) * scale + origin;

        private static Color Hex(int rgb)
            => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}

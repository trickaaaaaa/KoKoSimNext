using KokoSim.Engine.Players;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 調子の表情顔（設計書02 §3.3・パワプロ式5段階）。SDFフォントに絵文字が無いため Painter2D で描画する。
    /// 眺めてスキャンするだけで状態が拾える（UI原則⑥）。自校は常に真値、相手校は監督の育成眼に応じた
    /// 誤認あり（<see cref="FormModel.Observe"/>, issue #47）だが、どちらも必ず何らかの表情を描く。
    /// 色は tokens.uss の --cond-* を映す（RankPalette と同じミラー方針）。盤面/状態色はアクセント本数制限の対象外。
    /// 向きは「赤＝絶好調（熱い）… 青＝絶不調（冷えている）」＝ランクチップ（赤系が上・青系が下）と同じ
    /// パワプロ式。以前は信号機式（緑＝良・赤＝悪）で、同じ行のランク色と意味が逆になっていた（2026-08-15）。
    /// </summary>
    public sealed class ConditionFace : VisualElement
    {
        // tokens.uss --cond-best/good/normal/bad/worst のミラー（ConditionLabels.ColorHex とも一致させる）。design-22 G0 で新5色。
        private static readonly Color Excellent = new(0xE0 / 255f, 0x60 / 255f, 0x5A / 255f); // 赤
        private static readonly Color Good = new(0xE8 / 255f, 0x90 / 255f, 0x4A / 255f);      // 橙
        private static readonly Color Normal = new(0xE3 / 255f, 0xBD / 255f, 0x3C / 255f);    // 黄
        private static readonly Color Poor = new(0x7F / 255f, 0xA8 / 255f, 0xD6 / 255f);      // 水色
        private static readonly Color Terrible = new(0x4F / 255f, 0x6F / 255f, 0xA8 / 255f);  // 青
        private static readonly Color Ink = new(0x2A / 255f, 0x26 / 255f, 0x22 / 255f);        // 顔上の暗インク

        private Condition _condition = Condition.Normal;

        // 顔の輪郭リング（design-19 デーゲーム・フルNB）。明地では灰系の「普通」顔が地に溶けるため、
        // USS 側で `--face-ring: <色>` を与えた画面（.sk-day）だけインク枠を描く。
        // 既定は無指定＝リングなし＝夜スキンの見た目は不変（scope 保証）。
        private static readonly CustomStyleProperty<Color> RingProp = new("--face-ring");
        private Color _ring = new(0f, 0f, 0f, 0f);  // α=0＝リングなし

        // 色の上書き（任意）。USS で `--face-best` 等を与えた画面だけ差し替わる（オーダー画面のスコアブック試作）。
        // 指定のない画面は上の既定色（tokens.uss --cond-* のミラー）のまま。
        private static readonly CustomStyleProperty<Color>[] FaceProps =
        {
            new("--face-best"), new("--face-good"), new("--face-normal"), new("--face-bad"), new("--face-worst"),
        };
        private static readonly CustomStyleProperty<Color> InkProp = new("--face-ink");
        private readonly Color?[] _override = new Color?[5];
        private Color? _inkOverride;

        public ConditionFace()
        {
            AddToClassList("cond-face");
            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyle);
            pickingMode = PickingMode.Ignore;
        }

        private void OnCustomStyle(CustomStyleResolvedEvent e)
        {
            var dirty = false;
            if (e.customStyle.TryGetValue(RingProp, out var c) && c != _ring)
            {
                _ring = c;
                dirty = true;
            }
            for (var i = 0; i < FaceProps.Length; i++)
            {
                Color? v = e.customStyle.TryGetValue(FaceProps[i], out var fc) ? fc : null;
                if (v != _override[i]) { _override[i] = v; dirty = true; }
            }
            Color? ink = e.customStyle.TryGetValue(InkProp, out var ic) ? ic : null;
            if (ink != _inkOverride) { _inkOverride = ink; dirty = true; }
            if (dirty) MarkDirtyRepaint();
        }

        /// <summary>調子を設定。</summary>
        public void Set(Condition c)
        {
            _condition = c;
            MarkDirtyRepaint();
        }

        // 推定表示（相手校の調子＝監督の目で見た値。設計書02 §3.3・#47 の誤認モデルを通した値）。
        // 顔の色を55%に透かし、縁を同系色の点線にする＝「真値ではない」を字なしで示す。
        private const float EstimatedAlpha = 0.55f;
        private const float EstimatedRingDarken = 0.78f;   // 縁＝顔色を暗くした同系色
        private bool _estimated;

        /// <summary>推定表示の切り替え（相手校の選手＝true。自校は常に真値なので false）。</summary>
        public void SetEstimated(bool estimated)
        {
            if (_estimated == estimated) return;
            _estimated = estimated;
            MarkDirtyRepaint();
        }

        public bool Estimated => _estimated;

        private Color ResolvedFaceColor(Condition c)
        {
            var i = c switch
            {
                Condition.Excellent => 0, Condition.Good => 1, Condition.Poor => 3, Condition.Terrible => 4, _ => 2,
            };
            return _override[i] ?? FaceColor(c);
        }

        private static Color FaceColor(Condition c) => c switch
        {
            Condition.Excellent => Excellent,
            Condition.Good => Good,
            Condition.Poor => Poor,
            Condition.Terrible => Terrible,
            _ => Normal,
        };

        // -1(絶不調)〜+1(絶好調) の口角。表情差はここに集約。
        private static float MouthCurve(Condition c) => c switch
        {
            Condition.Excellent => 1f,
            Condition.Good => 0.5f,
            Condition.Poor => -0.5f,
            Condition.Terrible => -1f,
            _ => 0f,
        };

        private void OnGenerate(MeshGenerationContext ctx)
        {
            var c = _condition;
            var r = contentRect;
            if (r.width <= 0 || r.height <= 0) return;

            var p = ctx.painter2D;
            var cx = r.width * 0.5f;
            var cy = r.height * 0.5f;
            var rad = Mathf.Min(r.width, r.height) * 0.45f;

            // 顔の丸（状態色で塗り）。装飾（影/グロー）は付けない（UI原則③）。
            var faceColor = ResolvedFaceColor(c);
            p.fillColor = _estimated ? new Color(faceColor.r, faceColor.g, faceColor.b, EstimatedAlpha) : faceColor;
            p.BeginPath();
            p.Arc(new Vector2(cx, cy), rad, 0f, 360f);
            p.Fill();

            // 推定＝同系色の点線の縁（実線の輪郭 --face-ring より優先）。
            if (_estimated)
            {
                StrokeDashedCircle(p, new Vector2(cx, cy), rad,
                    new Color(faceColor.r * EstimatedRingDarken, faceColor.g * EstimatedRingDarken,
                        faceColor.b * EstimatedRingDarken, 1f),
                    Mathf.Max(1f, rad * 0.09f));
            }

            // 明地スキンのインク輪郭（--face-ring 指定時のみ）。灰系の「普通」も地から立つ。
            if (_ring.a > 0f && !_estimated)
            {
                p.strokeColor = _ring;
                p.lineWidth = Mathf.Max(1.5f, rad * 0.14f);
                p.BeginPath();
                p.Arc(new Vector2(cx, cy), rad, 0f, 360f);
                p.Stroke();
            }

            // 目（2点）。
            var eyeR = rad * 0.16f;
            var eyeDx = rad * 0.4f;
            var eyeDy = rad * 0.18f;
            var ink = _inkOverride ?? Ink;
            p.fillColor = ink;
            foreach (var sx in new[] { -1f, 1f })
            {
                p.BeginPath();
                p.Arc(new Vector2(cx + sx * eyeDx, cy - eyeDy), eyeR, 0f, 360f);
                p.Fill();
            }

            // 口（口角 curve に応じた円弧）。curve>0=笑顔（上向き）、<0=への字。
            var curve = MouthCurve(c);
            var mouthW = rad * 0.5f;
            var mouthY = cy + rad * 0.32f;
            p.strokeColor = ink;
            p.lineWidth = Mathf.Max(1.2f, rad * 0.14f);
            p.lineCap = LineCap.Round;
            p.BeginPath();
            if (Mathf.Abs(curve) < 0.05f)
            {
                // 普通＝横一文字。
                p.MoveTo(new Vector2(cx - mouthW, mouthY));
                p.LineTo(new Vector2(cx + mouthW, mouthY));
            }
            else
            {
                // 笑顔/への字＝二次ベジェで口角を上下。
                var ctrlY = mouthY + curve * rad * 0.5f;
                p.MoveTo(new Vector2(cx - mouthW, mouthY - curve * rad * 0.12f));
                p.BezierCurveTo(
                    new Vector2(cx - mouthW * 0.3f, ctrlY),
                    new Vector2(cx + mouthW * 0.3f, ctrlY),
                    new Vector2(cx + mouthW, mouthY - curve * rad * 0.12f));
            }
            p.Stroke();
        }

        // 点線の円。Painter2D に破線が無いので、円周を「線・間」の等長に割って弧を1本ずつ引く
        // （本数は円周から決める＝寸法が変わっても線と間の長さが保たれる）。
        private static void StrokeDashedCircle(Painter2D p, Vector2 c, float radius, Color color, float width)
        {
            var dash = Mathf.Max(1.5f, radius * 0.18f);
            var count = Mathf.Max(6, Mathf.RoundToInt(2f * Mathf.PI * radius / (dash * 2f)));
            var step = 360f / count;
            p.strokeColor = color;
            p.lineWidth = width;
            p.lineCap = LineCap.Butt;
            for (var i = 0; i < count; i++)
            {
                var a0 = i * step;
                p.BeginPath();
                p.Arc(c, radius, a0, a0 + step * 0.5f);
                p.Stroke();
            }
        }
    }
}

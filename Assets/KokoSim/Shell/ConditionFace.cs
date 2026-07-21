using KokoSim.Engine.Players;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 調子の表情顔（設計書02 §3.3・パワプロ式5段階）。SDFフォントに絵文字が無いため Painter2D で描画する。
    /// 眺めてスキャンするだけで状態が拾える（UI原則⑥）。自校は常に真値、相手校は監督の育成眼に応じた
    /// 誤認あり（<see cref="FormModel.Observe"/>, issue #47）だが、どちらも必ず何らかの表情を描く。
    /// 色は tokens.uss の状態色を映す（RankPalette と同じミラー方針）。盤面/状態色はアクセント本数制限の対象外。
    /// </summary>
    public sealed class ConditionFace : VisualElement
    {
        // tokens.uss の状態色ミラー（--color-good / --color-lamp / --color-muted / --color-warn）。
        private static readonly Color Excellent = new(0x69 / 255f, 0xB9 / 255f, 0x8B / 255f); // good
        private static readonly Color Good = new(0xA8 / 255f, 0xC6 / 255f, 0x4E / 255f);       // rank-d 黄緑
        private static readonly Color Normal = new(0x9A / 255f, 0xA5 / 255f, 0xA0 / 255f);     // muted
        private static readonly Color Poor = new(0xEC / 255f, 0x8B / 255f, 0x3C / 255f);       // rank-b 橙
        private static readonly Color Terrible = new(0xE8 / 255f, 0x6A / 255f, 0x4A / 255f);   // warn
        private static readonly Color Ink = new(0x14 / 255f, 0x23 / 255f, 0x1B / 255f);        // 顔上の暗インク

        private Condition _condition = Condition.Normal;

        public ConditionFace()
        {
            AddToClassList("cond-face");
            generateVisualContent += OnGenerate;
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>調子を設定。</summary>
        public void Set(Condition c)
        {
            _condition = c;
            MarkDirtyRepaint();
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
            p.fillColor = FaceColor(c);
            p.BeginPath();
            p.Arc(new Vector2(cx, cy), rad, 0f, 360f);
            p.Fill();

            // 目（2点）。
            var eyeR = rad * 0.16f;
            var eyeDx = rad * 0.4f;
            var eyeDy = rad * 0.18f;
            p.fillColor = Ink;
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
            p.strokeColor = Ink;
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
    }
}

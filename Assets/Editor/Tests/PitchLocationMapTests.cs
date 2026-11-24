using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Unity.Components;
using NUnit.Framework;
using UnityEngine;

namespace KokoSim.EditorTests
{
    /// <summary>投球チャート（対決ビューの右の中ほど）の座標変換と印の見た目。向きは投手の後ろから見た図。</summary>
    public class PitchLocationMapTests
    {
        private const float W = PitchLocationChart.ChartWidth, H = PitchLocationChart.ChartHeight;
        private static readonly StrikeZone Zone = new StrikeZone();

        private static Vector2 At(double x, double y) => PitchLocationMap.ToChart(Zone, (float)x, (float)y, W, H);

        [Test]
        public void ZoneCenter_IsChartCenter()
        {
            var p = At(0, Zone.CenterY);
            Assert.AreEqual(W * 0.5f, p.x, 0.01f);
            Assert.AreEqual(H * 0.5f, p.y, 0.01f);
        }

        [Test]
        public void InsideToRightHandedBatter_IsRightSideOfChart()
        {
            // 右打者は三塁側（-X）に立つ。投手の後ろから見ると打者は右＝内角（-X）は図の右半分
            Assert.Greater(At(-0.15, Zone.CenterY).x, W * 0.5f);
            Assert.Less(At(0.15, Zone.CenterY).x, W * 0.5f);
        }

        [Test]
        public void High_IsTopOfChart()
        {
            Assert.Less(At(0, Zone.TopM).y, At(0, Zone.BottomM).y);
        }

        [Test]
        public void ZoneCorners_AreSymmetricAndInsideChart()
        {
            var tl = At(Zone.HalfWidthM, Zone.TopM);
            var br = At(-Zone.HalfWidthM, Zone.BottomM);
            Assert.AreEqual(W - br.x, tl.x, 0.01f);
            Assert.AreEqual(H - br.y, tl.y, 0.01f);
            Assert.Greater(tl.x, 0f);
            Assert.Greater(tl.y, 0f);
        }

        [Test]
        public void Scale_IsUniformSoZoneKeepsItsAspect()
        {
            var tl = At(Zone.HalfWidthM, Zone.TopM);
            var br = At(-Zone.HalfWidthM, Zone.BottomM);
            var ratio = (br.x - tl.x) / (br.y - tl.y);
            Assert.AreEqual(Zone.HalfWidthM * 2.0 / (Zone.TopM - Zone.BottomM), ratio, 0.001);
        }

        [Test]
        public void OnlyFastballIsCircle()
        {
            Assert.IsFalse(PitchLocationMap.IsTriangle(PitchType.Fastball));
            Assert.IsTrue(PitchLocationMap.IsTriangle(PitchType.Slider));
            Assert.IsFalse(PitchLocationMap.IsTriangle(null));
        }

        [Test]
        public void ApexFollowsPitchChartDirection_AndMirrorsForLefty()
        {
            // スライダーは右投手で左（180°）、左投手で右。画面は上が -y
            Assert.AreEqual(-1f, PitchLocationMap.ApexDirection(PitchType.Slider, Handedness.Right).x, 0.001f);
            Assert.AreEqual(1f, PitchLocationMap.ApexDirection(PitchType.Slider, Handedness.Left).x, 0.001f);
            // フォークは真下（270°）＝画面の +y
            Assert.AreEqual(1f, PitchLocationMap.ApexDirection(PitchType.Fork, Handedness.Right).y, 0.001f);
        }

        [Test]
        public void ColorIsByResult()
        {
            Assert.AreEqual(PitchLocationMap.ColorOf(PitchKind.CalledStrike), PitchLocationMap.ColorOf(PitchKind.SwingingStrike));
            Assert.AreEqual(PitchLocationMap.ColorOf(PitchKind.Ball), PitchLocationMap.ColorOf(PitchKind.HitByPitch));
            Assert.AreNotEqual(PitchLocationMap.ColorOf(PitchKind.Ball), PitchLocationMap.ColorOf(PitchKind.CalledStrike));
            Assert.AreNotEqual(PitchLocationMap.ColorOf(PitchKind.Foul), PitchLocationMap.ColorOf(PitchKind.InPlay));
        }
    }
}

using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Unity.Match;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>選手交代のポップアップのリデザイン：説明文の部品列・比較の太字・カードの学年。</summary>
    public class SubstitutionDisplayTests
    {
        private static Player Make(string name, int number, int grade = 2)
            => new Player { Name = name, UniformNumber = number, Grade = grade };

        [Test]
        public void Weights_HigherIsBold_TieBoldsBoth_OneSideNeither()
        {
            Assert.AreEqual((true, false), SubstitutionDisplay.Weights(86, 49));
            Assert.AreEqual((false, true), SubstitutionDisplay.Weights(31, 40));
            Assert.AreEqual((true, true), SubstitutionDisplay.Weights(52, 52));
            Assert.AreEqual((false, false), SubstitutionDisplay.Weights(52, -1));
            Assert.AreEqual((false, false), SubstitutionDisplay.Weights(-1, 52));
        }

        [Test]
        public void AssignNote_HasPlayersPositionAndMutedRemark()
        {
            var parts = SubstitutionDisplay.Assign(Make("桑原 陸", 10), FieldPosition.RightField, Make("須賀 昊", 9));
            CollectionAssert.AreEqual(new[] { "10 桑原 陸", "9 須賀 昊" },
                parts.Where(p => p.Kind == NotePartKind.Player).Select(p => p.Text).ToArray());
            Assert.AreEqual("右", parts.Single(p => p.Kind == NotePartKind.Pos).Text);
            Assert.AreEqual("（再出場できない）", parts.Last().Text);
            Assert.AreEqual(NotePartKind.Muted, parts.Last().Kind);
            Assert.AreEqual("10 桑原 陸 が 右 に就き、 9 須賀 昊 は退場（再出場できない）", SubstitutionDisplay.Plain(parts));
        }

        [Test]
        public void Label_AndNumber_WithoutUniformNumber()
        {
            var p = Make("小田切 大河", 0);
            Assert.AreEqual("小田切 大河", SubstitutionDisplay.Label(p));
            Assert.AreEqual("—", SubstitutionDisplay.Number(p));
            Assert.AreEqual("—", SubstitutionDisplay.Label(null));
        }

        [Test]
        public void GradeAndHand_ShowsGradeOnlyWhenKnown()
        {
            StringAssert.StartsWith("3年・", SubstitutionDisplay.GradeAndHand(Make("a", 1, grade: 3)));
            StringAssert.DoesNotContain("年", SubstitutionDisplay.GradeAndHand(Make("a", 1, grade: 0)));
        }

        [Test]
        public void SwapNote_EachPlayerMovesToTheOthersPosition()
        {
            var parts = SubstitutionDisplay.FielderSwap(Make("A", 1), FieldPosition.LeftField, Make("B", 2), FieldPosition.CenterField);
            var pos = parts.Where(p => p.Kind == NotePartKind.Pos).Select(p => p.Text).ToArray();
            CollectionAssert.AreEqual(new[] { "中", "左" }, pos);
        }

        [Test]
        public void DhToMoundNote_DhPitchesAndCurrentPitcherLeaves()
        {
            var parts = SubstitutionDisplay.DhToMound(Make("A", 7), Make("B", 1));
            Assert.AreEqual("DHの 7 A が登板し、 1 B は退場（再登板できない・以降DHなし）", SubstitutionDisplay.Plain(parts));
        }
    }
}

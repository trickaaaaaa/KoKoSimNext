using KokoSim.Unity.Components;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>禁則（issue #1501）: 句読点・閉じ括弧は直前の1文字と、開き括弧は直後の1文字と &lt;nobr&gt; でひと続きになる。</summary>
    public class KinsokuTests
    {
        [Test]
        public void Period_IsGluedToThePreviousChar()
            => Assert.AreEqual("大会が開幕し<nobr>た。</nobr>", Kinsoku.Apply("大会が開幕した。"));

        [Test]
        public void ClosingBracketAndPeriodRun_IsGluedTogether()
            => Assert.AreEqual("<nobr>「勝</nobr><nobr>つ」。</nobr>", Kinsoku.Apply("「勝つ」。"));

        [Test]
        public void OpeningBracket_IsGluedToTheNextChar()
            => StringAssert.StartsWith("<nobr>「勝</nobr>", Kinsoku.Apply("「勝つ」"));

        [Test]
        public void ExistingTags_AreLeftUntouched()
            => Assert.AreEqual("<b>勝ち</b>", Kinsoku.Apply("<b>勝ち</b>"));

        [Test]
        public void PlainText_IsUnchanged()
            => Assert.AreEqual("今週の練習", Kinsoku.Apply("今週の練習"));

        [Test]
        public void Empty_IsUnchanged()
        {
            Assert.AreEqual("", Kinsoku.Apply(""));
            Assert.IsNull(Kinsoku.Apply(null));
        }
    }
}

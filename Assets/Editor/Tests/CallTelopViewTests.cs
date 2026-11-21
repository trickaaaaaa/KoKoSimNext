using KokoSim.Unity.Match.Telops;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace KokoSim.EditorTests
{
    /// <summary>判定テロップの画像面（issue #1179）。畳んだあとに前の面が出直さない（issue #1201）。</summary>
    public class CallTelopViewTests
    {
        [Test]
        public void Tick_AfterHide_DoesNotShowPreviousFace()
        {
            var image = new VisualElement();
            var view = new CallTelopView(image);

            // 1球目: ストライク
            view.SetFace("tl-call-strike");
            view.Tick(0.3, 0.85, 0.12, 0.12);
            Assert.AreEqual(1f, image.style.opacity.value, 1e-4);

            // 2球目: インプレー＝呼び出し側は Hide して、下段のために時計だけ回し続ける
            view.Hide();
            view.Tick(0.3, 0.85, 0.12, 0.12);

            Assert.AreEqual(0f, image.style.opacity.value, 1e-4, "インプレーで前の球の判定が出直す");
            Assert.IsFalse(image.ClassListContains("tl-call-strike"));
            Assert.IsNull(view.ActiveFace);
        }

        [Test]
        public void SetFace_AfterHide_ShowsNewFace()
        {
            var image = new VisualElement();
            var view = new CallTelopView(image);
            view.SetFace("tl-call-strike");
            view.Hide();

            view.SetFace("tl-call-strike");   // 同じ面をもう一度（ストライク→インプレー→ストライクの並び）
            view.Tick(0.3, 0.85, 0.12, 0.12);

            Assert.IsTrue(image.ClassListContains("tl-call-strike"));
            Assert.AreEqual(1f, image.style.opacity.value, 1e-4);
        }
    }
}

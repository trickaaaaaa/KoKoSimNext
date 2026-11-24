using System.Linq;
using KokoSim.Unity.Shell;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// ブラウザ版の校名は入力欄の代わりに候補から選ぶ（#1323・2026-11-24 オーナー決定）。
    /// ブラウザ版には日本語入力が無く、入力欄では英数字しか打てない。
    /// </summary>
    public class BrowserNamePickerTests
    {
        private const int Tochigi = 8;

        [TearDown]
        public void ResetPlatform() => BuildPlatform.ForceBrowserInEditor = false;

        private static (VisualElement Line, TextField Field, VisualElement Host) Build()
        {
            var line = new VisualElement();
            var field = new TextField { name = "nm" };
            line.Add(field);
            var host = new VisualElement();
            return (line, field, host);
        }

        [Test]
        public void InBrowser_HidesTheFieldAndShowsItsValue()
        {
            BuildPlatform.ForceBrowserInEditor = true;
            var (line, field, host) = Build();
            var picker = new BrowserNamePicker(field, host, "btn", "shown");

            Assert.IsTrue(picker.Active);
            Assert.AreEqual(DisplayStyle.None, field.style.display.value, "入力欄が見えたまま");
            var shown = line.Q<Label>("nm-shown");
            Assert.IsNotNull(shown, "値を出すラベルが入力欄の隣に無い");
            Assert.AreEqual("―", shown.text);

            picker.Choose("宇都宮");
            Assert.AreEqual("宇都宮", field.value, "選んだ候補が入力欄に入らない（検証・確定は入力欄を読む）");
            Assert.AreEqual("宇都宮", shown.text, "選んだ候補が表示に反映されない");

            field.SetValueWithoutNotify("小山");
            picker.Sync();
            Assert.AreEqual("小山", shown.text);
        }

        [Test]
        public void InBrowser_CandidatesAreThePrefecturesPlacesAndSetTheValue()
        {
            BuildPlatform.ForceBrowserInEditor = true;
            var (_, field, host) = Build();
            var picker = new BrowserNamePicker(field, host, "btn");
            picker.SetCandidates(Tochigi);

            var buttons = host.Query<Button>(className: "name-cand").ToList();
            var expected = BrowserNamePicker.CandidatesOf(Tochigi);
            Assert.That(expected.Count, Is.EqualTo(BrowserNamePicker.CandidateCount), "校名語彙の地名が足りない");
            CollectionAssert.AreEqual(expected, buttons.Select(b => b.text).ToList());

            // ボタンを押したときの値の入り方は撮影レシピ browser-name-candidates-1323 が実際に押して確かめる
            // （画面に載っていない要素にはクリックが配られない）
        }

        [Test]
        public void OutsideBrowser_LeavesTheFieldAlone()
        {
            var (line, field, host) = Build();
            var picker = new BrowserNamePicker(field, host, "btn");
            picker.SetCandidates(Tochigi);

            Assert.IsFalse(picker.Active);
            Assert.AreNotEqual(DisplayStyle.None, field.style.display.value);
            Assert.IsNull(line.Q<Label>("nm-shown"));
            Assert.AreEqual(0, host.childCount, "Windows 版に候補ボタンを足している");
        }
    }
}

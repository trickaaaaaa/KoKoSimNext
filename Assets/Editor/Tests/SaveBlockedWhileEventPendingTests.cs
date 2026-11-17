using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// イベントの場面が残っている間はセーブできないことを固定する（issue #1115）。
    /// キューはセーブに載らないので、残したまま保存するとロードで選択肢つきイベントが選ばれないまま消えた。
    /// </summary>
    public class SaveBlockedWhileEventPendingTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        [TearDown]
        public void Cleanup() => EventService.Clear();

        [Test]
        public void CanSave_IsFalseWhileChoiceEventIsPending_AndTrueAfterItIsCompleted()
        {
            Assert.IsTrue(SaveGameIO.CanSave, "前提: 新規ゲーム直後は保存できる");

            Assert.IsTrue(EventService.PushChoiceEvent("grade_friction", 0.0), "選択肢つきイベントを積めた");
            Assert.IsTrue(EventService.HasPending);
            Assert.IsFalse(SaveGameIO.CanSave, "場面が残っている間は保存できない");
            Assert.IsNotEmpty(SaveGameIO.SaveDisabledReason(), "保存できない理由が出る");

            EventService.Complete();
            Assert.IsFalse(EventService.HasPending);
            Assert.IsTrue(SaveGameIO.CanSave, "見終えたら保存できる");
            Assert.IsEmpty(SaveGameIO.SaveDisabledReason());
        }

        [Test]
        public void Save_Refuses_WhileEventIsPending()
        {
            Assert.IsTrue(EventService.PushChoiceEvent("grade_friction", 0.0));
            var path = System.IO.Path.Combine(UnityEngine.Application.temporaryCachePath, "issue1115.sav");
            Assert.IsFalse(SaveGameIO.Save(path), "場面が残ったままは書かない");
            Assert.IsFalse(System.IO.File.Exists(path));
        }
    }
}

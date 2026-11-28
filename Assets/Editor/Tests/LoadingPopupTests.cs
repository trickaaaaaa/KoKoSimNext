using System;
using System.Collections;
using KokoSim.Engine.Saves;
using KokoSim.Unity.Components;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// ロードの汎用ポップアップ（<see cref="LoadingPopup"/>）のちらつき防止と、<see cref="LoadingJob"/> の進み方、
    /// セーブ枠のカードの表示データ（<see cref="SaveSlotCardData"/>）。
    /// </summary>
    public class LoadingPopupTests
    {
        [SetUp]
        public void SetUp() => LoadingJob.ResetForTests();

        [TearDown]
        public void TearDown() => LoadingJob.ResetForTests();

        [Test]
        public void ShortRequest_NeverShows()
        {
            LoadingPopup.Show("ロード中");
            Assert.IsFalse(LoadingPopup.ResolveVisibleForTests(10.0), "要求した直後は出さない");
            Assert.IsFalse(LoadingPopup.ResolveVisibleForTests(10.2));
            LoadingPopup.Hide();
            Assert.IsFalse(LoadingPopup.ResolveVisibleForTests(10.25), "0.3秒より前に終わった処理は一度も出ない");
        }

        [Test]
        public void LongRequest_ShowsAfterDelay_AndStaysAtLeastMinimum()
        {
            LoadingPopup.Show("セーブ中");
            Assert.IsFalse(LoadingPopup.ResolveVisibleForTests(10.0));
            Assert.IsTrue(LoadingPopup.ResolveVisibleForTests(10.31), "0.3秒たっても終わっていなければ出す");
            LoadingPopup.Hide();
            Assert.IsTrue(LoadingPopup.ResolveVisibleForTests(10.5), "出してから0.5秒は残す");
            Assert.IsFalse(LoadingPopup.ResolveVisibleForTests(10.82), "0.5秒たったら消す");
        }

        [Test]
        public void ImmediateRequest_ShowsAtOnce()
        {
            LoadingPopup.Show("ロード中", null, immediate: true);
            Assert.IsTrue(LoadingPopup.ResolveVisibleForTests(10.0), "メインスレッドを止める処理は待たずに出す");
        }

        [Test]
        public void Job_RunsStepsAcrossFrames_AndHidesAtEnd()
        {
            var log = new System.Collections.Generic.List<int>();
            var gate = false;
            Assert.IsTrue(LoadingJob.Run("セーブ中", null, Steps(log, () => gate)));
            Assert.IsFalse(LoadingJob.Run("ロード中", null, Steps(log, () => true)), "走っている間は二重に始めない");
            Assert.IsTrue(LoadingPopup.Requested);

            for (var i = 0; i < 5; i++) LoadingJob.Tick();
            Assert.AreEqual(new[] { 1 }, log.ToArray(), "条件が立つまで先へ進まない");
            gate = true;
            for (var i = 0; i < 5; i++) LoadingJob.Tick();
            Assert.AreEqual(new[] { 1, 2 }, log.ToArray());
            Assert.IsFalse(LoadingJob.Running);
            Assert.IsFalse(LoadingPopup.Requested, "終わったら消す要求を出す");
        }

        private static IEnumerator Steps(System.Collections.Generic.List<int> log, Func<bool> gate)
        {
            log.Add(1);
            yield return gate;
            log.Add(2);
        }

        [Test]
        public void Job_StartsOnlyAfterPopupHadFrames()
        {
            var ran = false;
            LoadingJob.Run("ロード中", null, Once(() => ran = true));
            LoadingJob.Tick();
            Assert.IsFalse(ran, "押したコマでは始めない（先にポップアップを描く）");
            LoadingJob.Tick();
            LoadingJob.Tick();
            Assert.IsTrue(ran);
        }

        private static IEnumerator Once(Action a)
        {
            a();
            yield break;
        }

        // ── セーブ枠のカードの表示データ ──

        [Test]
        public void CardData_OldSave_HasDashesAndCharcoal()
        {
            var info = new SaveSlotInfo("manual1", exists: true, corrupt: false, week: 3, yearIndex: 1,
                savedAt: new DateTime(2026, 11, 22, 0, 26, 0), schoolName: "検証");
            var d = SaveSlotCardData.From(info, 1);
            Assert.IsNull(d.Overall);
            Assert.IsNull(d.Wins);
            Assert.IsNull(d.SpringAppearances);
            Assert.IsNull(d.Phase);
            Assert.IsNull(d.Prefecture);
            Assert.AreEqual(SchoolAccent.Charcoal, d.BandColor, "色の無い古いセーブは炭色");
        }

        [Test]
        public void CardData_ReadsSummaryColor()
        {
            var sum = new SaveSummary("秋の大会", 63, 68, 28, 0, 0) { Color = "#7A1F2B" };
            var info = new SaveSlotInfo("manual2", true, false, 23, 13, new DateTime(2026, 11, 27), "宮前商工", 13, sum);
            var d = SaveSlotCardData.From(info, 2);
            Assert.AreEqual(SaveSummaries.GradeOf(63), d.Grade);
            Assert.AreEqual(63, d.Overall);
            Assert.AreEqual(68, d.Wins);
            Assert.AreEqual("秋の大会", d.Phase);
            Assert.AreEqual(0x7A / 255f, d.BandColor.r, 0.002f);
        }

        [TestCase("検証", "検証高校")]
        [TestCase("検証高校", "検証高校")]   // #1358 より前の新規作成は「高校」込みで保存していた
        public void CardData_SchoolName_NeverDoublesKoukou(string stored, string shown)
        {
            var info = new SaveSlotInfo("manual1", true, false, 0, 1, DateTime.Now, stored);
            Assert.AreEqual(shown, SaveSlotCardData.From(info, 1).SchoolName);
        }

        [Test]
        public void DaysAgo_TodayYesterdayN()
        {
            var now = new DateTime(2026, 11, 28, 10, 0, 0);
            Assert.AreEqual("今日", SaveSlotCard.DaysAgo(new DateTime(2026, 11, 28, 0, 1, 0), now));
            Assert.AreEqual("昨日", SaveSlotCard.DaysAgo(new DateTime(2026, 11, 27, 23, 41, 0), now));
            Assert.AreEqual("6日前", SaveSlotCard.DaysAgo(new DateTime(2026, 11, 22, 0, 26, 0), now));
        }
    }
}

using System;
using System.Threading;
using KokoSim.Unity.Shell;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KokoSim.EditorTests
{
    /// <summary>BackgroundGameOp の見張り（Q30・issue #1317）。戻らない work を上限秒で検知し、スロットは解放しない。</summary>
    public class BackgroundGameOpStallTests
    {
        private Func<double> _savedClock;
        private double _now;

        [SetUp]
        public void SetUp()
        {
            _savedClock = BackgroundGameOp.Clock;
            _now = 1000;
            BackgroundGameOp.Clock = () => _now;
        }

        [TearDown]
        public void TearDown() => BackgroundGameOp.Clock = _savedClock;

        private static void DrainUntilIdle()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (BackgroundGameOp.Running && DateTime.UtcNow < deadline)
            {
                MainThreadDispatcher.Drain();
                Thread.Sleep(5);
            }
            Assert.IsFalse(BackgroundGameOp.Running, "work を解放したのに Running が落ちない");
        }

        [Test]
        public void Stalls_OnlyAfterLimit_LogsOnce_AndDoesNotReleaseSlot()
        {
            using (var gate = new ManualResetEventSlim(false))
            {
                Assert.IsTrue(BackgroundGameOp.Run(() => gate.Wait(), null));
                try
                {
                    Assert.IsFalse(BackgroundGameOp.Stalled);

                    _now += BackgroundGameOp.StallLimitSeconds - 1;
                    Assert.IsFalse(BackgroundGameOp.Stalled, "上限の手前では固まり扱いにしない");
                    Assert.IsFalse(BackgroundGameOp.CheckStall());

                    _now += 2;
                    Assert.IsTrue(BackgroundGameOp.Stalled);
                    LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("固まり"));
                    Assert.IsTrue(BackgroundGameOp.CheckStall(), "初検知でログを出す");
                    Assert.IsFalse(BackgroundGameOp.CheckStall(), "ログは1度だけ");

                    Assert.IsTrue(BackgroundGameOp.Running, "スロットは強制解放しない");
                    Assert.IsFalse(BackgroundGameOp.Run(() => { }, null), "固まり中も二重起動は弾く");
                }
                finally
                {
                    gate.Set();
                    DrainUntilIdle();
                }
            }
            Assert.IsFalse(BackgroundGameOp.Stalled, "戻れば固まりは解ける");
        }

        [Test]
        public void NextRun_ResetsStallState()
        {
            using (var gate = new ManualResetEventSlim(false))
            {
                BackgroundGameOp.Run(() => gate.Wait(), null);
                _now += BackgroundGameOp.StallLimitSeconds + 1;
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("固まり"));
                BackgroundGameOp.CheckStall();
                gate.Set();
                DrainUntilIdle();
            }

            using (var gate = new ManualResetEventSlim(false))
            {
                BackgroundGameOp.Run(() => gate.Wait(), null);
                try
                {
                    Assert.IsFalse(BackgroundGameOp.Stalled, "新しい処理は開始時刻から数え直す");
                    _now += BackgroundGameOp.StallLimitSeconds + 1;
                    LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("固まり"));
                    Assert.IsTrue(BackgroundGameOp.CheckStall(), "次の処理でも再び検知してログを出す");
                }
                finally
                {
                    gate.Set();
                    DrainUntilIdle();
                }
            }
        }

        [Test]
        public void ThrowingWork_ReleasesSlot_AndStillCallsOnDone()
        {
            var done = false;
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("boom"));
            Assert.IsTrue(BackgroundGameOp.Run(() => throw new InvalidOperationException("boom"), () => done = true));
            DrainUntilIdle();
            Assert.IsTrue(done, "work が例外でも onDone は呼ばれる");
            Assert.IsTrue(BackgroundGameOp.Run(() => { }, null), "解除後は次の処理を始められる");
            DrainUntilIdle();
        }

        [Test]
        public void OnDone_CanStartNextRun()
        {
            var nextStarted = false;
            Assert.IsTrue(BackgroundGameOp.Run(() => { }, () => nextStarted = BackgroundGameOp.Run(() => { }, null)));
            DrainUntilIdle();
            Assert.IsTrue(nextStarted, "onDone の時点でスロットは解けている");
            DrainUntilIdle();
        }
    }
}

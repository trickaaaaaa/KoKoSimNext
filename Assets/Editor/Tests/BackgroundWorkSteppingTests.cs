using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// ブラウザ版は重い裏の仕事を1コマで回し切らない（#1588）。ブラウザ版の Unity は GC をコマの切れ目にしか
    /// 回せず、夏の県大会で負けた後の［次へ］で甲子園代表48校の打ち直しを1コマで回してメモリ（2GB）を使い切った。
    /// <see cref="BackgroundWork.SimulateBrowser"/> でブラウザ版の回し方を Editor で真似て確かめる。
    /// </summary>
    public class BackgroundWorkSteppingTests
    {
        [SetUp]
        public void SetUp()
        {
            BackgroundWork.ResetForTests();
            BackgroundWork.SimulateBrowser = true;
        }

        [TearDown]
        public void TearDown()
        {
            BackgroundWork.SimulateBrowser = false;
            BackgroundWork.ResetForTests();
        }

        private static Func<bool> Counter(int steps, List<int> log)
        {
            var i = 0;
            return () =>
            {
                log.Add(i);
                return ++i < steps;
            };
        }

        [Test]
        public void Browser_RunSteps_DoesNotRunInPlace_AndPumpAdvancesFramesBySlices()
        {
            var log = new List<int>();
            var t = BackgroundWork.RunSteps(Counter(5, log));

            Assert.IsEmpty(log, "呼んだその場では回さない（1コマで回し切らない）");
            Assert.IsTrue(BackgroundWork.Busy);

            BackgroundWork.Pump(0);   // 予算0でも1歩は進む（止まらない）
            Assert.AreEqual(1, log.Count, "1コマで予算ぶんだけ回してコマを返す");
            Assert.IsFalse(t.IsCompleted);

            for (var frame = 0; frame < 10 && !t.IsCompleted; frame++) BackgroundWork.Pump(0);
            Assert.IsTrue(t.IsCompleted);
            Assert.AreEqual(new[] { 0, 1, 2, 3, 4 }, log.ToArray());
            Assert.IsFalse(BackgroundWork.Busy);
        }

        [Test]
        public void Browser_Wait_DrainsInPlace()
        {
            var log = new List<int>();
            var t = BackgroundWork.RunSteps(Counter(4, log));
            BackgroundWork.Wait(t);
            Assert.IsTrue(t.IsCompleted);
            Assert.AreEqual(4, log.Count);
        }

        [Test]
        public void Browser_StepThatThrows_FaultsItsTask_AndOthersContinue()
        {
            var bad = BackgroundWork.RunSteps(() => throw new InvalidOperationException("x"));
            var log = new List<int>();
            var good = BackgroundWork.RunSteps(Counter(2, log));
            for (var frame = 0; frame < 5; frame++) BackgroundWork.Pump(0);
            Assert.IsTrue(bad.IsFaulted);
            Assert.IsTrue(good.IsCompleted && !good.IsFaulted);
            Assert.Throws<AggregateException>(() => BackgroundWork.Wait(bad));
        }

        [Test]
        public void Browser_DeferUntilIdle_RunsOnceAfterDrained()
        {
            var log = new List<int>();
            BackgroundWork.RunSteps(Counter(3, log));
            var runs = 0;
            Action advance = () => runs++;

            Assert.IsTrue(BackgroundWork.DeferUntilIdle(advance));
            Assert.IsTrue(BackgroundWork.DeferUntilIdle(advance), "連打");
            Assert.IsTrue(BackgroundWork.HasWaiters, "待ち画面を出す合図");
            Assert.AreEqual(0, runs);

            BackgroundWork.Pump(0);
            Assert.AreEqual(0, runs, "回し切るまで走らせない");
            for (var frame = 0; frame < 5; frame++) BackgroundWork.Pump(0);
            Assert.AreEqual(1, runs, "回し切った後に1回だけ走る");
            Assert.IsFalse(BackgroundWork.HasWaiters);

            Assert.IsFalse(BackgroundWork.DeferUntilIdle(advance), "待つ仕事が無ければ積まない（呼び元がそのまま続ける）");
        }

        [Test]
        public void Browser_BackgroundGameOp_StartsWorkOnlyAfterDrained()
        {
            var log = new List<int>();
            BackgroundWork.RunSteps(Counter(3, log));
            var workRan = false;
            var done = false;

            Assert.IsTrue(BackgroundGameOp.Run(() => workRan = log.Count == 3, () => done = true));
            Assert.IsTrue(BackgroundGameOp.Running, "スロットは先に取る＝二度押しを弾く");
            Assert.IsFalse(BackgroundGameOp.Run(() => { }, null));

            for (var frame = 0; frame < 10 && !done; frame++)
            {
                BackgroundWork.Pump(0);
                MainThreadDispatcher.Drain();
            }
            Assert.IsTrue(done);
            Assert.IsTrue(workRan, "work は裏の仕事を回し切ってから始まる");
            Assert.IsFalse(BackgroundGameOp.Running);
        }

        [Test]
        public void Threaded_RunSteps_RunsToTheEndOnAThread()
        {
            BackgroundWork.SimulateBrowser = false;
            var count = 0;
            var t = BackgroundWork.RunSteps(() => Interlocked.Increment(ref count) < 50);
            BackgroundWork.Wait(t);
            Assert.AreEqual(50, count);
            Assert.IsFalse(BackgroundWork.Busy, "スレッド版は歩み待ちを持たない");
            Assert.IsFalse(BackgroundWork.DeferUntilIdle(() => { }), "スレッド版は操作を待たせない");
        }

        /// <summary>Task を直接待つと、ブラウザ版では歩みが回らず永久に止まる。<see cref="BackgroundWork.Wait"/> を通す。</summary>
        [Test]
        public void RuntimeCode_WaitsForBackgroundTasksOnlyThroughBackgroundWork()
        {
            var direct = new Regex(@"\.Wait\(\s*\)");
            var offenders = Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories)
                .Select(p => p.Replace('\\', '/'))
                .Where(p => !p.Contains("/Editor/") && !p.EndsWith("Shooter.cs") && !p.EndsWith("Probe.cs")
                            && !p.EndsWith("Verifier.cs") && !p.EndsWith("/BackgroundWork.cs"))
                .SelectMany(p => File.ReadAllLines(p)
                    .Select((line, i) => (p, i, line))
                    .Where(x => !x.line.TrimStart().StartsWith("//") && direct.IsMatch(x.line)))
                .Select(x => $"{x.p}:{x.i + 1}: {x.line.Trim()}")
                .ToList();
            Assert.IsEmpty(offenders,
                "裏の仕事を Task.Wait() で直接待っている（ブラウザ版で止まる）。BackgroundWork.Wait を使う:\n"
                + string.Join("\n", offenders));
        }
    }
}

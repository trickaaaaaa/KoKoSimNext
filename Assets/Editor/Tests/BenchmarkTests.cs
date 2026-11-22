using KokoSim.Unity.Benchmark;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>ベンチマークモード（issue #1314）の引数の読み方と集計の固定。画面を回す本体（BenchmarkRunner）は実機で確かめる。</summary>
    public class BenchmarkTests
    {
        [Test]
        public void TryParse_WithoutFlag_IsNotBenchmark()
        {
            Assert.IsFalse(BenchmarkOptions.TryParse(new[] { "game.exe", "-screen-width", "1280" }, out var o));
            Assert.IsNull(o);
        }

        [Test]
        public void TryParse_FlagOnly_UsesDefaults()
        {
            Assert.IsTrue(BenchmarkOptions.TryParse(new[] { "game.exe", "-benchmark" }, out var o));
            Assert.AreEqual(BenchmarkOptions.DefaultSeed, o.Seed);
            Assert.AreEqual(30, o.MaxWeeks);
            Assert.AreEqual(2, o.TournamentMatches);
            Assert.AreEqual(120f, o.MatchSeconds);
            Assert.IsNull(o.OutDir);
        }

        [Test]
        public void TryParse_ReadsValues()
        {
            var args = new[]
            {
                "game.exe", "-benchmark", "-benchmark-out", "C:\\bench out", "-benchmark-seed", "0xABC",
                "-benchmark-weeks", "20", "-benchmark-matches", "0", "-benchmark-match-seconds", "45.5",
                "-benchmark-idle-seconds", "1", "-benchmark-speed", "2",
            };
            Assert.IsTrue(BenchmarkOptions.TryParse(args, out var o));
            Assert.AreEqual("C:\\bench out", o.OutDir);
            Assert.AreEqual(0xABCUL, o.Seed);
            Assert.AreEqual(20, o.MaxWeeks);
            Assert.AreEqual(0, o.TournamentMatches);
            Assert.AreEqual(45.5f, o.MatchSeconds);
            Assert.AreEqual(1f, o.IdleSeconds);
            Assert.AreEqual(2f, o.MatchSpeed);
        }

        [Test]
        public void TryParse_BadValue_KeepsDefault()
        {
            Assert.IsTrue(BenchmarkOptions.TryParse(
                new[] { "-benchmark", "-benchmark-weeks", "abc", "-benchmark-seed", "zzz", "-benchmark-weeks", "0" }, out var o));
            Assert.AreEqual(30, o.MaxWeeks);
            Assert.AreEqual(BenchmarkOptions.DefaultSeed, o.Seed);
        }

        [Test]
        public void Summarize_ConstantFrames_GivesExactFps()
        {
            var frames = new float[100];
            for (var i = 0; i < frames.Length; i++) frames[i] = 0.02f;   // 50fps

            var s = BenchmarkStats.Summarize(frames);

            Assert.AreEqual(100, s.Count);
            Assert.AreEqual(50.0, s.AvgFps, 1e-3);
            Assert.AreEqual(20.0, s.P50Ms, 1e-3);
            Assert.AreEqual(20.0, s.WorstMs, 1e-3);
            Assert.AreEqual(50.0, s.Fps1PctLow, 1e-3);
        }

        [Test]
        public void Summarize_OneSpike_ShowsInWorstAndP99ButNotMedian()
        {
            // 99 コマが 10ms、1 コマが 200ms。最近傍順位なので p99 は 100 番目の手前（10ms）、最悪は 200ms。
            var frames = new float[100];
            for (var i = 0; i < frames.Length; i++) frames[i] = 0.01f;
            frames[40] = 0.2f;

            var s = BenchmarkStats.Summarize(frames);

            Assert.AreEqual(10.0, s.P50Ms, 1e-3);
            Assert.AreEqual(10.0, s.P99Ms, 1e-3);
            Assert.AreEqual(200.0, s.WorstMs, 1e-3);
        }

        [Test]
        public void Summarize_Empty_IsAllZero()
        {
            var s = BenchmarkStats.Summarize(new float[0]);
            Assert.AreEqual(0, s.Count);
            Assert.AreEqual(0.0, s.AvgFps);
            Assert.AreEqual(0.0, s.Fps1PctLow);
        }

        [Test]
        public void Percentile_IsNearestRank()
        {
            var v = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            Assert.AreEqual(5.0, BenchmarkStats.Percentile(v, 50));
            Assert.AreEqual(10.0, BenchmarkStats.Percentile(v, 95));
            Assert.AreEqual(1.0, BenchmarkStats.Percentile(v, 0));
        }

        [Test]
        public void CsvField_QuotesOnlyWhenNeeded()
        {
            Assert.AreEqual("Intel UHD 620", BenchmarkStats.CsvField("Intel UHD 620"));
            Assert.AreEqual("\"a,b\"", BenchmarkStats.CsvField("a,b"));
            Assert.AreEqual("\"say \"\"hi\"\"\"", BenchmarkStats.CsvField("say \"hi\""));
            Assert.AreEqual("", BenchmarkStats.CsvField(null));
        }

        [Test]
        public void Num_IgnoresCulture()
        {
            var old = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.AreEqual("12.50", BenchmarkStats.Num(12.5));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = old; }
        }
    }
}

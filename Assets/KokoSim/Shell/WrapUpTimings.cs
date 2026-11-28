using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 大会の後始末（結果ポップアップの OK → 週送り・地区大会・神宮・センバツ選考）の段ごとの所要時間を残す観測口
    /// （issue #1747）。乱数にも結果にも触れない。計測の読み出しは <see cref="Report"/>（撮影レシピの
    /// <c>read: =WrapUpTimings.Report</c>）。
    /// </summary>
    public static class WrapUpTimings
    {
        private static readonly object Gate = new object();
        private static readonly List<KeyValuePair<string, double>> Entries = new List<KeyValuePair<string, double>>();

        public static IDisposable Measure(string label, double minMs = 0) => new Scope(label, minMs);

        public static void Clear()
        {
            lock (Gate) Entries.Clear();
        }

        /// <summary>「段 = ミリ秒」を記録順に1行ずつ（同じ段は足し込まず別行）。</summary>
        public static string Report
        {
            get
            {
                var sb = new StringBuilder();
                lock (Gate)
                    foreach (var e in Entries) sb.Append(e.Key).Append(" = ").Append(e.Value.ToString("0.0")).Append("ms; ");
                return sb.ToString();
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _label;
            private readonly double _minMs;
            private readonly Stopwatch _sw = Stopwatch.StartNew();

            public Scope(string label, double minMs) { _label = label; _minMs = minMs; }

            public void Dispose()
            {
                _sw.Stop();
                var ms = _sw.Elapsed.TotalMilliseconds;
                if (ms < _minMs) return;
                lock (Gate) Entries.Add(new KeyValuePair<string, double>(_label, ms));
            }
        }
    }
}

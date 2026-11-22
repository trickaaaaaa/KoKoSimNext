using System;
using System.Collections.Generic;
using System.Globalization;

namespace KokoSim.Unity.Benchmark
{
    /// <summary>1つの区間（画面・場面）のフレーム時間の要約。</summary>
    public readonly struct FrameSummary
    {
        public int Count { get; }
        public double TotalSeconds { get; }
        public double AvgFps { get; }
        public double P50Ms { get; }
        public double P95Ms { get; }
        public double P99Ms { get; }
        public double WorstMs { get; }

        /// <summary>下位1%（フレーム時間の99パーセンタイル）の fps。カクつきを平均より厳しく見る値。</summary>
        public double Fps1PctLow => P99Ms > 0 ? 1000.0 / P99Ms : 0;

        public FrameSummary(int count, double totalSeconds, double avgFps,
            double p50Ms, double p95Ms, double p99Ms, double worstMs)
        {
            Count = count; TotalSeconds = totalSeconds; AvgFps = avgFps;
            P50Ms = p50Ms; P95Ms = p95Ms; P99Ms = p99Ms; WorstMs = worstMs;
        }
    }

    /// <summary>ベンチマークの集計と CSV の書式（Unity に触れない純粋関数。EditMode テストで固定する）。</summary>
    public static class BenchmarkStats
    {
        /// <summary>フレーム時間（秒）の列から要約を作る。空なら全部 0。</summary>
        public static FrameSummary Summarize(IReadOnlyList<float> frameSeconds)
        {
            if (frameSeconds == null || frameSeconds.Count == 0) return default;

            var ms = new double[frameSeconds.Count];
            double total = 0;
            for (var i = 0; i < ms.Length; i++)
            {
                total += frameSeconds[i];
                ms[i] = frameSeconds[i] * 1000.0;
            }
            Array.Sort(ms);

            var avgFps = total > 0 ? ms.Length / total : 0;
            return new FrameSummary(ms.Length, total, avgFps,
                Percentile(ms, 50), Percentile(ms, 95), Percentile(ms, 99), ms[ms.Length - 1]);
        }

        /// <summary>昇順に並んだ値の最近傍順位パーセンタイル（補間しない＝実際に出た値だけを返す）。</summary>
        public static double Percentile(double[] sortedAscending, double percent)
        {
            if (sortedAscending == null || sortedAscending.Length == 0) return 0;
            var rank = (int)Math.Ceiling(percent * sortedAscending.Length / 100.0);
            return sortedAscending[Math.Min(Math.Max(rank, 1), sortedAscending.Length) - 1];
        }

        /// <summary>CSV の1項目。カンマ・引用符・改行を含むときだけ引用符で囲む（RFC 4180）。</summary>
        public static string CsvField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>数値は小数点をロケールに左右されない形で出す（2台の機の CSV を同じ書式で比べるため）。</summary>
        public static string Num(double v, int decimals = 2)
            => v.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}

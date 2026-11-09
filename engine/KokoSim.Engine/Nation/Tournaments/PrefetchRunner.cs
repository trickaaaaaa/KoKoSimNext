using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 裏カードのプレフェッチを並列で回す小道具（試合開始前の待ち短縮）。結果は捨てる＝メモ化 resolver を温めるだけ。
/// 各ジョブは独立 Fork の rng と読み取り専用の context だけを使うので、実行順・並列度で結果は変わらない。
/// </summary>
internal static class PrefetchRunner
{
    public static void Run<T>(IReadOnlyList<T> jobs, Action<T> resolve, int maxParallelism)
    {
        if (jobs.Count == 0) return;
        if (maxParallelism <= 1 || jobs.Count == 1)
        {
            foreach (var j in jobs) resolve(j);
            return;
        }
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
            i => resolve(jobs[i]));
    }
}

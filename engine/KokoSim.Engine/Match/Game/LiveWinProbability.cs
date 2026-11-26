using System;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 高速試合モードの勝率バー用の簡易の期待勝率（issue #1494）。点差・回・表裏・アウト・走者から、後攻が勝つ確率を出す。
/// <b>表示専用</b>＝乱数を引かず、試合の進行・結果・指紋に触れない。
///
/// <para>終盤ほど点差が効く正規近似（残りの半回ぶんの得点のばらつき）を、ロジスティックで写したもの
/// （<see cref="Nation.AggregateMatch.WinProbability"/> と同じ対数オッズの流儀）。現在の半回の得点期待は
/// 走者・アウト別の得点期待表から引く。試合前の勝ちやすさ（<c>homePrior</c>）は
/// 残りの半回が減るほど薄まる＝試合開始時はちょうど <c>homePrior</c> に一致する。</para>
/// </summary>
public static class LiveWinProbability
{
    private const double RunsPerHalfInning = 0.5;       // 1半回の平均得点
    private const double VariancePerHalfInning = 1.1;   // 1半回の得点の分散
    private const double LogisticScale = 1.702;         // 正規CDF ≒ logistic(1.702x)
    private const double HalvesInNineInnings = 18.0;
    private const double MinProbability = 0.0005;
    private const int ExtraInningTail = 2;              // 延長は決着の読めない半回を2つ残す

    // 得点期待（走者の並び×アウト0〜2）。添字＝(一塁 | 二塁<<1 | 三塁<<2) * 3 + アウト。
    private static readonly double[] RunExpectancy =
    {
        0.50, 0.27, 0.10,   // 走者なし
        0.90, 0.54, 0.24,   // 一塁
        1.10, 0.66, 0.32,   // 二塁
        1.45, 0.93, 0.45,   // 一二塁
        1.35, 0.95, 0.36,   // 三塁
        1.80, 1.14, 0.49,   // 一三塁
        1.96, 1.38, 0.54,   // 二三塁
        2.30, 1.55, 0.75,   // 満塁
    };

    /// <summary>
    /// 直前に確定した打席（<see cref="PlayLogEntry"/>）の直後の場面から出す。3アウトで終わっていれば次の半回の頭
    /// （アウト0・走者なし）、そうでなければその打席後のアウト・走者。ログが空なら試合開始。
    /// 打席の途中の状態（<c>CurrentAtBat</c>）を読まずに済む＝早送りの直後でも安全に呼べる。
    /// </summary>
    public static double HomeWinProbability(
        PlayLogEntry? lastPlay, int awayRuns, int homeRuns,
        int regulationInnings = 9, double homePrior = 0.5)
    {
        if (lastPlay is null)
            return HomeWinProbability(awayRuns, homeRuns, 1, true, 0, false, false, false, regulationInnings, homePrior);
        var outs = lastPlay.OutsBefore + lastPlay.OutsThisPa;
        if (outs >= 3)
        {
            var nextIsTop = !lastPlay.IsTop;
            var inning = lastPlay.IsTop ? lastPlay.Inning : lastPlay.Inning + 1;
            return HomeWinProbability(awayRuns, homeRuns, inning, nextIsTop, 0, false, false, false, regulationInnings, homePrior);
        }
        return HomeWinProbability(awayRuns, homeRuns, lastPlay.Inning, lastPlay.IsTop, outs,
            lastPlay.BaseFirstAfter, lastPlay.BaseSecondAfter, lastPlay.BaseThirdAfter, regulationInnings, homePrior);
    }

    /// <summary>後攻が勝つ確率（0〜1）。</summary>
    /// <param name="homePrior">試合前の後攻の勝率（既定0.5＝力の差を見ない）。</param>
    public static double HomeWinProbability(
        int awayRuns, int homeRuns, int inning, bool isTop, int outs,
        bool first, bool second, bool third,
        int regulationInnings = 9, double homePrior = 0.5)
    {
        var o = Math.Clamp(outs, 0, 2);
        var mask = (first ? 1 : 0) | (second ? 2 : 0) | (third ? 4 : 0);
        var currentRuns = RunExpectancy[mask * 3 + o];

        int awayFuture, homeFuture;
        if (inning <= regulationInnings)
        {
            awayFuture = regulationInnings - inning;
            homeFuture = regulationInnings - inning + (isTop ? 1 : 0);
        }
        else
        {
            awayFuture = 0;
            homeFuture = isTop ? 1 : 0;
        }

        var remaining = awayFuture + homeFuture + (3 - o) / 3.0;
        if (inning > regulationInnings) remaining += ExtraInningTail;

        var margin = homeRuns - awayRuns
            + RunsPerHalfInning * (homeFuture - awayFuture)
            + (isTop ? -currentRuns : currentRuns);

        var prior = Math.Clamp(homePrior, MinProbability, 1.0 - MinProbability);
        var priorLogit = Math.Log(prior / (1.0 - prior));
        var logit = LogisticScale * margin / Math.Sqrt(VariancePerHalfInning * remaining)
            + priorLogit * Math.Sqrt(remaining / HalvesInNineInnings);
        return Math.Clamp(MathUtil.Logistic(logit), MinProbability, 1.0 - MinProbability);
    }
}

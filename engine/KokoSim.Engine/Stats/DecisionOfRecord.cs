using System.Collections.Generic;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 勝敗投手の決定（設計書の save/win 規則は未整備＝簡易規則, OPEN-QUESTIONS #4）。
/// 規則: 先発がチーム総アウトの半分以上を投げていれば先発が決定投手、そうでなければ最終登板投手。
/// 自校が勝てば勝ち投手、負ければ負け投手を返す（引分は該当なし）。相手校は追跡しない（null）。
/// </summary>
public static class DecisionOfRecord
{
    /// <summary>自校側の勝敗投手 SourceId を返す（勝ち/負けのうち成立した方のみ非null）。</summary>
    public static (int? WinPid, int? LosePid) Resolve(GameResult r, bool managerIsAway)
    {
        var managerRuns = managerIsAway ? r.AwayRuns : r.HomeRuns;
        var opponentRuns = managerIsAway ? r.HomeRuns : r.AwayRuns;
        if (managerRuns == opponentRuns) return (null, null); // 引分＝決定なし

        var pitching = managerIsAway ? r.AwayPitching : r.HomePitching;
        var decisionPid = DecisionIndex(pitching) is int i ? pitching[i].SourceId : null;

        return managerRuns > opponentRuns ? (decisionPid, null) : (null, decisionPid);
    }

    /// <summary>
    /// 登板順リストの中で決定投手が何番目かを返す（該当なし＝空リストのみ null）。
    /// 勝敗投手マークは相手校でも出すため、SourceId を持たないラインでも使えるよう添字で返す。
    /// </summary>
    public static int? DecisionIndex(IReadOnlyList<PitchingLine> pitching)
    {
        if (pitching.Count == 0) return null;

        var totalOuts = 0;
        foreach (var l in pitching) totalOuts += l.Outs;

        // 先発が総アウトの半分以上なら先発、そうでなければ最終登板投手。
        return pitching[0].Outs * 2 >= totalOuts ? 0 : pitching.Count - 1;
    }

    /// <summary>
    /// 両校の勝敗投手の添字（勝ち/負けそれぞれの登板順インデックス。引分・登板なしは null）。
    /// 試合結果画面の勝敗投手マーク用。表示専用で試合結果には影響しない。
    /// </summary>
    public static (int? AwayWin, int? AwayLose, int? HomeWin, int? HomeLose) ResolveIndices(GameResult r)
    {
        if (r.Tied) return (null, null, null, null);

        var away = DecisionIndex(r.AwayPitching);
        var home = DecisionIndex(r.HomePitching);
        return r.HomeWon ? (null, away, home, null) : (away, null, null, home);
    }
}

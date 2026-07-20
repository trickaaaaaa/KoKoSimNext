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
        if (pitching.Count == 0) return (null, null);

        var totalOuts = 0;
        foreach (var l in pitching) totalOuts += l.Outs;

        // 先発が総アウトの半分以上なら先発、そうでなければ最終登板投手。
        var starter = pitching[0];
        var decisionLine = starter.Outs * 2 >= totalOuts ? starter : pitching[pitching.Count - 1];
        var decisionPid = decisionLine.SourceId;

        return managerRuns > opponentRuns ? (decisionPid, null) : (null, decisionPid);
    }
}

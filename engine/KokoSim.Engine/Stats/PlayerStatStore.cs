using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 自校選手の成績ストア（3スコープ）。通算（Career, 練習試合を含む全試合・永続）、
/// 公式戦通算（Official, 大会の試合だけ・永続）、今大会（CurrentTournament, 大会ごとにリセット）を
/// 並行して積む。純データ・決定論・UnityEngine 非依存。
/// インスタンスは横断状態として Shell（GameSession）が保持する。
/// </summary>
public sealed class PlayerStatStore
{
    /// <summary>通算成績（永続。練習試合も含む全試合。夏/秋・年を跨いでも消さない）。</summary>
    public StatBook Career { get; } = new();

    /// <summary>公式戦通算成績（永続。練習試合は含まない）。</summary>
    public StatBook Official { get; } = new();

    /// <summary>今大会成績（大会開始時にクリア）。</summary>
    public StatBook CurrentTournament { get; } = new();

    /// <summary>大会開始時に今大会スコープをリセット（通算・公式戦通算は保持）。</summary>
    public void StartTournament() => CurrentTournament.Clear();

    /// <summary>
    /// 1試合ぶんの詳細結果を各スコープへ畳み込む。勝敗投手は自動判定（<see cref="DecisionOfRecord"/>）。
    /// <paramref name="isOfficial"/>=false（練習試合）のときは通算スコープにだけ積み、
    /// 公式戦通算・今大会には積まない（設計: 練習試合は公式記録に残さない）。
    /// </summary>
    public void FoldGame(GameResult r, bool managerIsAway, bool isOfficial = true)
    {
        var (winPid, losePid) = DecisionOfRecord.Resolve(r, managerIsAway);
        Career.FoldGame(r, managerIsAway, winPid, losePid);
        if (!isOfficial) return;
        Official.FoldGame(r, managerIsAway, winPid, losePid);
        CurrentTournament.FoldGame(r, managerIsAway, winPid, losePid);
    }
}

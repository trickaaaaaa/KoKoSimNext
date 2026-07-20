using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 自校選手の成績ストア（2スコープ）。通算（Career, ゲーム全体で永続）と今大会（CurrentTournament,
/// 大会ごとにリセット）を並行して積む。純データ・決定論・UnityEngine 非依存。
/// インスタンスは横断状態として Shell（GameSession）が保持する。
/// </summary>
public sealed class PlayerStatStore
{
    /// <summary>通算成績（永続。夏/秋・年を跨いでも消さない）。</summary>
    public StatBook Career { get; } = new();

    /// <summary>今大会成績（大会開始時にクリア）。</summary>
    public StatBook CurrentTournament { get; } = new();

    /// <summary>大会開始時に今大会スコープをリセット（通算は保持）。</summary>
    public void StartTournament() => CurrentTournament.Clear();

    /// <summary>
    /// 1試合ぶんの詳細結果を両スコープへ畳み込む。勝敗投手は自動判定（<see cref="DecisionOfRecord"/>）。
    /// </summary>
    public void FoldGame(GameResult r, bool managerIsAway)
    {
        var (winPid, losePid) = DecisionOfRecord.Resolve(r, managerIsAway);
        Career.FoldGame(r, managerIsAway, winPid, losePid);
        CurrentTournament.FoldGame(r, managerIsAway, winPid, losePid);
    }
}

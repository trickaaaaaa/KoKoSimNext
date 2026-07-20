using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会1試合の記録（設計書13-stadiums §2-2）。球場はメタ情報で、勝敗には効かない（AggregateMatch 不変・no-fudge）。
/// UI/大会プレビュー・将来の詳細再生が参照する。RoundsRemaining は knockout の深さ（決勝=1・準決勝=2…浅いほど大）。
/// </summary>
public sealed record TournamentMatchRecord(
    string Stage,
    int RoundsRemaining,
    School Winner,
    School Loser,
    int Margin,
    string StadiumId);

/// <summary>
/// 県大会の使用球場プラン（pref-format 由来）。序盤ラウンド=early プールから抽選、準決勝/決勝=final 固定。
/// 空（None）なら球場割当なし（StadiumId は空文字）。
/// </summary>
public sealed record StadiumPlan(string? FinalId, IReadOnlyList<string> EarlyIds)
{
    public static readonly StadiumPlan None = new(null, System.Array.Empty<string>());

    public bool IsEmpty => FinalId is null && EarlyIds.Count == 0;

    /// <summary>ラウンドに球場を割り当てる。残り≤2ラウンド（準決勝・決勝）は final、序盤は early を抽選。</summary>
    public string Assign(int roundsRemaining, IRandomSource rng)
    {
        if (FinalId is not null && roundsRemaining is >= 1 and <= 2) return FinalId;
        if (EarlyIds.Count > 0) return EarlyIds[rng.NextInt(0, EarlyIds.Count)];
        return FinalId ?? "";
    }
}

/// <summary>
/// 試合ごとの記録＋球場割当を集めるシンク（設計書13-stadiums §2-2）。大会エンジンに任意で注入する。
/// 球場抽選は専用の fork ストリームで行い、本編の乱数列を汚さない（不変条件#2・決定論。Fork は親状態を進めない）。
/// recorder=null なら従来動作（球場割当・記録なし）。
/// </summary>
public sealed class TournamentRecorder
{
    private readonly List<TournamentMatchRecord> _records = new();
    private readonly StadiumPlan _plan;
    private readonly IRandomSource _stadiumRng;

    /// <summary>現在処理中のステージ名（PrefTournamentEngine が各ステージ前に設定）。</summary>
    public string CurrentStage { get; set; } = "";

    public TournamentRecorder(StadiumPlan plan, IRandomSource stadiumRng)
    {
        _plan = plan;
        _stadiumRng = stadiumRng;
    }

    public IReadOnlyList<TournamentMatchRecord> Records => _records;

    public void Record(int roundsRemaining, School winner, School loser, int margin)
    {
        var stadiumId = _plan.Assign(roundsRemaining, _stadiumRng);
        _records.Add(new TournamentMatchRecord(CurrentStage, roundsRemaining, winner, loser, margin, stadiumId));
    }

    /// <summary>bracket の残チーム数から決勝までのラウンド数を返す（2→1, 4→2, 8→3…）。</summary>
    public static int RoundsRemaining(int teamsInBracket)
    {
        var rounds = 0;
        var n = teamsInBracket;
        while (n > 1) { n >>= 1; rounds++; }
        return rounds;
    }
}

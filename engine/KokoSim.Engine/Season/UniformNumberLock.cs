using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Engine.Season;

/// <summary>
/// 背番号とベンチ入り（20人）を固める期間の判定（設計書05 §1.2・設計書09 §6）。実際の高校野球と同じく、
/// ベンチ入りは大会の前に登録し、大会の途中では入れ替えられない。自校が大会に残っている間だけ固まり、
/// 負けたあと・優勝したあと・大会と大会の間は登録し直せる。画面はこの判定だけを見る。
/// </summary>
public static class UniformNumberLock
{
    /// <summary>自校の大会が進行中（まだ負けも優勝もしていない）なら true。大会に入っていない（null）なら false。</summary>
    public static bool IsLocked(ITournamentRunner? runner) => runner is { Finished: false };
}

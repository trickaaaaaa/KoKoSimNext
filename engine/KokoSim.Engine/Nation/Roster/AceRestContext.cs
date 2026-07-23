using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// 先発選択（エース温存, issue #42）の入力。<see cref="OpponentTier"/> はこれから対戦する相手のティア
/// （温存の可否を判断する校自身から見た相手）。null なら常時エース先発（従来挙動＝展望・練習試合等）。
/// </summary>
public sealed record AceRestContext(Tier OpponentTier, int RoundsRemaining, int MatchDay, TournamentPitchLedger? Ledger)
{
    /// <summary>大会側の試合コンテキストと相手ティアから組み立てる（<paramref name="match"/> が null なら null）。</summary>
    public static AceRestContext? From(TournamentMatchContext? match, Tier opponentTier)
        => match is null ? null : new AceRestContext(opponentTier, match.RoundsRemaining, match.MatchDay, match.Ledger);
}

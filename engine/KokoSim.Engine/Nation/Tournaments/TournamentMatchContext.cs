namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会の1試合ぶんの進行コンテキスト（ラウンド残数・試合日・投手台帳）。エース温存判断（issue #42）等、
/// 大会状態に依存する采配判断のための入力。<see cref="RoundsRemaining"/> はこの試合を含む残数（決勝=1）。
/// 渡さない呼び出し（練習試合等、大会に紐付かない一戦）はそれらの判断を行わない（既定null安全）。
/// </summary>
public sealed record TournamentMatchContext(int RoundsRemaining, int MatchDay, TournamentPitchLedger Ledger);

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会の1試合ぶんの進行コンテキスト（ラウンド残数・試合日・投手台帳）。エース温存判断（issue #42）等、
/// 大会状態に依存する采配判断のための入力。<see cref="RoundsRemaining"/> はこの試合を含む残数（決勝=1）。
/// 渡さない呼び出し（練習試合等、大会に紐付かない一戦）はそれらの判断を行わない（既定null安全）。
/// <see cref="TieReplay"/> は引き分け再試合（設計書05 §1.3, issue #253）の再試合回（0＝初戦）。カードの
/// メモ化キー（<see cref="MemoizingBackgroundResolver"/>）と Fork ストリームを再試合ごとに分けるための添字で、
/// 0 のときは従来と完全一致する（初戦は決定論baseline不変）。
/// </summary>
public sealed record TournamentMatchContext(int RoundsRemaining, int MatchDay, TournamentPitchLedger Ledger, int TieReplay = 0);

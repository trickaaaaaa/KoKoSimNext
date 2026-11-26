using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会の1試合ぶんの進行コンテキスト（ラウンド残数・試合日・投手台帳）。エース温存判断（issue #42）等、
/// 大会状態に依存する采配判断のための入力。<see cref="RoundsRemaining"/> はこの試合を含む残数（決勝=1）。
/// 渡さない呼び出し（練習試合等、大会に紐付かない一戦）はそれらの判断を行わない（既定null安全）。
/// <see cref="TieReplay"/> は引き分け再試合（設計書05 §1.3, issue #253）の再試合回（0＝初戦）。カードの
/// メモ化キー（<see cref="MemoizingBackgroundResolver"/>）と Fork ストリームを再試合ごとに分けるための添字で、
/// 0 のときは従来と完全一致する（初戦は決定論baseline不変）。
/// <see cref="AceRestSalt"/> は温存抽選のシードに混ぜる大会ごとの値（0＝従来と同じ）。
/// <see cref="AceRestRoundsRemaining"/> は温存判断だけが見る「この先に残る試合数」（null＝<see cref="RoundsRemaining"/>）。
/// リーグ戦は <see cref="RoundsRemaining"/> を意味のない定数にしているので、温存にはリーグの実際の残り節数を渡す。
/// </summary>
public sealed record TournamentMatchContext(
    int RoundsRemaining, int MatchDay, TournamentPitchLedger Ledger, int TieReplay = 0, ulong AceRestSalt = 0,
    int? AceRestRoundsRemaining = null)
{
    /// <summary>温存判断が使う残り試合数。</summary>
    public int RestRoundsRemaining => AceRestRoundsRemaining ?? RoundsRemaining;

    /// <summary>エース温存の抽選シードに混ぜる大会ごとの値の基準ストリームID。</summary>
    public const ulong AceRestSaltStream = TournamentForkKeys.AceRestSalt;

    /// <summary>
    /// 大会の基準乱数（母種＾年＾種別で決まる）から温存抽選用の塩を導く。Fork は親を進めない純導出＝本編の乱数列は動かない。
    /// これを混ぜないと温存の抽選値が (校ID, 試合日) だけで決まり、毎年・夏秋で同じになる。
    /// </summary>
    public static ulong AceRestSaltOf(IRandomSource tournamentRng) => tournamentRng.Fork(AceRestSaltStream).NextUInt64();
}

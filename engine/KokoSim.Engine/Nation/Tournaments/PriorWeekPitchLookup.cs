using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会台帳（<see cref="TournamentPitchLedger"/>）から、ロスター全員ぶんの<b>持ち越し球数</b>
/// （週間球数制限・設計書05 §1.3）を一括で求める。結果は <see cref="GameEngine.Play"/>／
/// <see cref="Timeline.Playback.MatchProgression"/> の priorWeekPitches 引数へそのまま渡せる形
/// （<see cref="Player"/> → 直近1週間の球数）。純関数・決定論（乱数を一切消費しない）。
///
/// <para><see cref="PitchRecoveryLookup"/>（球数→実効目安投球数）と<b>同型・別目的</b>。
/// あちらは「疲れて目安が縮む」連続量、こちらは「ルール上もう投げられない」離散量。
/// 台帳の引き方（キー・窓）は同じなので、キー関数は呼び出し側で共有する。</para>
/// </summary>
public static class PriorWeekPitchLookup
{
    /// <summary>
    /// 1チームぶんのロスターを台帳で引く。ledger が null なら空辞書を返す
    /// （＝持ち越しなし＝単一試合では上限に届かない＝従来挙動と完全一致）。
    /// </summary>
    public static Dictionary<Player, int> BuildFor(
        IEnumerable<Player> roster, System.Func<Player, PitcherLedgerKey> keyOf,
        TournamentPitchLedger? ledger, int matchDay, int windowDays)
    {
        var map = new Dictionary<Player, int>();
        if (ledger is null) return map;

        foreach (var player in roster)
        {
            var pitches = ledger.PitchesWithin(keyOf(player), matchDay, windowDays);
            if (pitches > 0) map[player] = pitches;
        }
        return map;
    }

    /// <summary>
    /// 両チームぶんを<b>チームごとに別の辞書</b>で返す（<see cref="GameEngine.Play"/> の
    /// awayPriorWeekPitches/homePriorWeekPitches へそのまま渡す形）。1つの辞書へ merge しないのは
    /// <see cref="PitchRecoveryLookup.ForMatch"/> と同じ理由＝<see cref="Player"/> が値等価の record で、
    /// 別校の選手同士がたまたま全属性一致すると同一キーに潰れかねないため。
    /// context が null なら両方とも空辞書（＝大会外の試合。従来挙動と完全一致）。
    /// </summary>
    public static (Dictionary<Player, int> Away, Dictionary<Player, int> Home) ForMatch(
        TournamentMatchContext? context, int windowDays,
        IEnumerable<Player> awayRoster, System.Func<Player, PitcherLedgerKey> awayKeyOf,
        IEnumerable<Player> homeRoster, System.Func<Player, PitcherLedgerKey> homeKeyOf)
    {
        if (context is null) return (new Dictionary<Player, int>(), new Dictionary<Player, int>());

        var away = BuildFor(awayRoster, awayKeyOf, context.Ledger, context.MatchDay, windowDays);
        var home = BuildFor(homeRoster, homeKeyOf, context.Ledger, context.MatchDay, windowDays);
        return (away, home);
    }
}

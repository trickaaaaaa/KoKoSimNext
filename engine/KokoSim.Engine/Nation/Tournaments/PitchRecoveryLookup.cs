using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 大会台帳（<see cref="TournamentPitchLedger"/>）から、ロスター全員ぶんの実効目安投球数
/// （<see cref="PitchRecoveryModel"/>）を一括で求める（設計書02 §1.1e-2, issue #41 本流配線）。
/// 結果は <see cref="GameEngine.Play"/>/<see cref="GameEngine.NewProgress"/> の pitchRecovery 引数へ
/// そのまま渡せる形（<see cref="Player"/> → 実効目安投球数）。純関数・決定論（乱数を一切消費しない）。
/// </summary>
public static class PitchRecoveryLookup
{
    /// <summary>
    /// 1チームぶんのロスターを台帳で引く。ledger が null なら空辞書を返す
    /// （＝GameEngine 側は素の目安投球数のまま。序盤の一発トーナメント・Balance CLI と完全一致）。
    /// </summary>
    public static Dictionary<Player, double> BuildFor(
        IEnumerable<Player> roster, System.Func<Player, PitcherLedgerKey> keyOf,
        TournamentPitchLedger? ledger, int matchDay, PitchRecoveryCoefficients coeff)
    {
        var map = new Dictionary<Player, double>();
        if (ledger is null) return map;

        // 「直近ウィンドウ」は設計書02 §1.1e-2 の記述どおり回復日数と同じ既定7日を使う
        // （回復モデルに窓専用の係数は無い＝ FullRecoveryDays を流用）。
        var windowDays = (int)coeff.FullRecoveryDays;
        foreach (var player in roster)
        {
            var key = keyOf(player);
            var restDays = ledger.RestDays(key, matchDay) ?? int.MaxValue;   // 未登板＝フレッシュ扱い
            var recentPitches = ledger.PitchesWithin(key, matchDay, windowDays);
            map[player] = PitchRecoveryModel.EffectiveStaminaPitches(
                player.Pitching.StaminaPitches, restDays, recentPitches, coeff);
        }
        return map;
    }

    /// <summary>
    /// 両チームぶんを<b>チームごとに別の辞書</b>で返す（<see cref="GameEngine.Play"/> の
    /// awayPitchRecovery/homePitchRecovery へそのまま渡す形）。1つの辞書へ merge しないのは、
    /// <see cref="Player"/> が値等価の record で、別校の選手同士がたまたま全属性一致すると
    /// 同一キーに潰れかねないため（校をまたいだ衝突を構造的に起こさない設計。校内では背番号が
    /// 一意なので衝突しない）。context が null なら両方とも空辞書（＝大会外の試合。従来挙動と完全一致）。
    /// </summary>
    public static (Dictionary<Player, double> Away, Dictionary<Player, double> Home) ForMatch(
        TournamentMatchContext? context, PitchRecoveryCoefficients coeff,
        IEnumerable<Player> awayRoster, System.Func<Player, PitcherLedgerKey> awayKeyOf,
        IEnumerable<Player> homeRoster, System.Func<Player, PitcherLedgerKey> homeKeyOf)
    {
        if (context is null) return (new Dictionary<Player, double>(), new Dictionary<Player, double>());

        var away = BuildFor(awayRoster, awayKeyOf, context.Ledger, context.MatchDay, coeff);
        var home = BuildFor(homeRoster, homeKeyOf, context.Ledger, context.MatchDay, coeff);
        return (away, home);
    }

    /// <summary>チームの全選手（打順＋控え＋DH時の先発投手）を重複なく列挙する。</summary>
    public static IEnumerable<Player> AllPlayers(Team team)
    {
        var seen = new HashSet<Player>();
        foreach (var p in team.BattingOrder) if (seen.Add(p)) yield return p;
        foreach (var p in team.Reserves) if (seen.Add(p)) yield return p;
        if (team.StartingPitcher is { } sp && seen.Add(sp)) yield return sp;
    }
}

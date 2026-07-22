using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 選手交代の「添字ベース適用」単一入口（設計書09 §6）。
///
/// 交代は <see cref="GameSaveState"/> の決定列（<see cref="GameDecision"/>）へ添字で保存され、復元時に
/// 同じ打席境界で再適用される（不変条件#2 決定論）。ライブ操作（<c>MatchProgression</c>）と復元
/// （<see cref="GameReplay"/>）が**同じ関数**を通ることで、「同シード＋同交代操作 → 同結果」が構造的に保証される。
///
/// ここでの添字はすべて「その時点の可変状態」に対するもの:
/// 控え野手＝<see cref="TeamState.Bench"/>、投手交代の候補＝<see cref="TeamState.AvailablePitcherCandidates"/>
/// （ブルペン＋野手控え）、打順スロット＝0-8、塁＝0(一塁)/1(二塁)/2(三塁)。
/// 乱数は一切消費しない（交代は決定的操作）。
/// </summary>
internal static class SubstitutionCommands
{
    /// <summary>次打者へ代打（teamIsAway=交代するチームが先攻か）。</summary>
    public static bool PinchHit(GameProgress p, bool teamIsAway, int benchIndex)
    {
        var team = p.OffenseOf(teamIsAway);   // OffenseOf(isTop): isTop=true=先攻away
        return TryBench(team, benchIndex, out var sub) && team.PinchHitNext(sub);
    }

    /// <summary>塁上の走者へ代走。塁の参照も差し替える（BaseState は呼び出し側の責務＝設計書09 C-1）。</summary>
    public static bool PinchRun(GameProgress p, bool teamIsAway, int baseIndex, int benchIndex)
    {
        var team = p.OffenseOf(teamIsAway);
        var bases = p.CurrentBases;
        if (bases is null || baseIndex < 0 || baseIndex > 2) return false;
        var runner = RunnerAt(bases, baseIndex);
        if (runner is null) return false;
        if (!TryBench(team, benchIndex, out var sub)) return false;
        if (!team.PinchRunFor(runner, sub)) return false;
        // 打順スロットは守備位置を継承して置換されるので、塁上の参照も同じ実体へ揃える。
        SetRunnerAt(bases, baseIndex, sub with { Position = runner.Position });
        return true;
    }

    /// <summary>
    /// 投手交代候補（ブルペン＋野手控え, <see cref="TeamState.AvailablePitcherCandidates"/>）から指名して継投する
    /// （teamIsAway=交代するチーム＝守備側が先攻か。issue #137: 野手も投手として登板できる）。
    /// </summary>
    public static bool ChangePitcher(GameProgress p, bool teamIsAway, int candidateIndex)
    {
        var team = p.OffenseOf(teamIsAway);
        var candidates = team.AvailablePitcherCandidates;
        return candidateIndex >= 0 && candidateIndex < candidates.Count && team.ChangePitcherTo(candidates[candidateIndex]);
    }

    /// <summary>守備交代（打順スロット slot の選手を控えへ）。</summary>
    public static bool DefensiveSub(GameProgress p, bool teamIsAway, int slot, int benchIndex)
    {
        var team = p.OffenseOf(teamIsAway);
        if (slot < 0 || slot >= team.CurrentLineup.Count) return false;
        var outgoing = team.CurrentLineup[slot];
        return TryBench(team, benchIndex, out var sub) && team.DefensiveSub(outgoing, sub);
    }

    /// <summary>DH解除（不可逆）。at 指定でDHがその守備位置に就く／null でDHはそのまま退場。</summary>
    public static bool ReleaseDh(GameProgress p, bool teamIsAway, FieldPosition? at)
        => p.OffenseOf(teamIsAway).ReleaseDh(at);

    private static bool TryBench(TeamState team, int benchIndex, out Players.Player sub)
    {
        var bench = team.Bench;
        if (benchIndex < 0 || benchIndex >= bench.Count) { sub = null!; return false; }
        sub = bench[benchIndex];
        return true;
    }

    private static Players.Player? RunnerAt(BaseState b, int i)
        => i switch { 0 => b.First, 1 => b.Second, _ => b.Third };

    private static void SetRunnerAt(BaseState b, int i, Players.Player p)
    {
        if (i == 0) b.First = p;
        else if (i == 1) b.Second = p;
        else b.Third = p;
    }
}

using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Replay;

/// <summary>
/// 保存した試合の再生（issue #1580）。記録した入力から、同じエンジンで試合を解き直す。
/// 版（<see cref="MatchReplayVersion"/>）が今と違う記録は「この版では再生できない」＝null を返す（黙って別の試合を出さない）。
/// </summary>
public static class MatchReplayer
{
    /// <summary>この記録を今のエンジンで再生できるか。</summary>
    public static bool CanReplay(MatchReplayRecord record) => MatchReplayVersion.Matches(record);

    /// <summary>
    /// 試合を解き直して結果を返す。再生できない版・壊れた記録は null。
    /// </summary>
    /// <param name="baseCtx">係数の基準（実ゲームは既定＝C# 既定）。観測のフラグ（タイムライン等）を立てて渡してよい＝乱数中立。</param>
    public static GameResult? Replay(MatchReplayRecord record, GameContext? baseCtx = null)
    {
        if (!CanReplay(record) || record.Away is null || record.Home is null || record.RngState.Length == 0) return null;

        var ctx = record.Context.ApplyTo(baseCtx ?? new GameContext());
        var away = record.Away.ToTeam();
        var home = record.Home.ToTeam();
        var rng = Xoshiro256Random.FromState(record.RngState);

        if (record.Decisions.Count == 0)
            return GameEngine.Play(away, home, ctx, rng,
                record.Away.PriorWeekPitches, record.Home.PriorWeekPitches,
                record.Away.PitchRecovery, record.Home.PitchRecovery);

        // プレイヤーの采配が入った試合は、決定列を元の打席・元の球で当て直しながら最後まで進める。
        // 1球ごとの采配窓が無いと PitchIndex>0 の決定が落ちるので、窓だけは必ず開ける（乱数中立）。
        var resumed = GameReplay.Restore(
            away, home, ctx with { CapturePitches = true },
            new GameSaveState(0UL, int.MaxValue) { Decisions = record.Decisions, RngState = record.RngState },
            scenarioStart: null,
            awayPitchRecovery: record.Away.PitchRecovery, homePitchRecovery: record.Home.PitchRecovery,
            awayPriorWeekPitches: record.Away.PriorWeekPitches, homePriorWeekPitches: record.Home.PriorWeekPitches);
        while (resumed.Steps.MoveNext()) { /* 残りを進める */ }
        return GameEngine.BuildResult(resumed.Progress);
    }
}

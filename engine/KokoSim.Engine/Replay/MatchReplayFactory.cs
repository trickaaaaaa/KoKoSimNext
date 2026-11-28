using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Engine.Replay;

/// <summary>
/// 裏試合（自校が出ない試合）の再生データを残す口（issue #1580）。<see cref="BackgroundMatchResolver"/> が試合のたびに
/// <see cref="Evaluate"/> で残す理由を尋ね、<see cref="NotableReason.None"/> 以外のときだけ解いた入力を <see cref="Collect"/> へ渡す。
/// 大会の種別・年度・日付は resolver が知らないので、実装（Shell）が <see cref="Collect"/> で記録へ足してから台帳へ積む。
/// 背景スレッドから呼ばれる＝実装はスレッド安全にすること。結果・乱数には一切触れない（決定論不変）。
/// </summary>
public interface IMatchReplayCollector
{
    NotableReason Evaluate(School away, School home);

    void Collect(MatchReplayRecord record);
}

/// <summary>解いた試合から <see cref="MatchReplayRecord"/> を組む（乱数を使わない純関数）。</summary>
public static class MatchReplayFactory
{
    /// <summary>
    /// 試合の入力と結果から記録を作る。大会側の情報（年度・種別・日付・ラウンド名）は <paramref name="meta"/> の値を引き継ぐ。
    /// 再生で組み直せない試合（采配が敵AI以外・デバッグの強制発動つき・乱数状態なし）は null。
    /// </summary>
    public static MatchReplayRecord? Create(
        MatchReplayInputs inputs, ReplayBrainSpec? awayBrain, ReplayBrainSpec? homeBrain, GameResult result,
        NotableReason reasons, int awaySchoolId, int homeSchoolId, MatchReplayRecord? meta = null)
    {
        if (inputs.RngState.Length == 0) return null;
        foreach (var d in inputs.Decisions)
            if (d.Forced is not null) return null;

        var away = ReplayTeamSpec.Capture(inputs.Away, awayBrain, inputs.AwayPitchRecovery, inputs.AwayPriorWeekPitches);
        var home = ReplayTeamSpec.Capture(inputs.Home, homeBrain, inputs.HomePitchRecovery, inputs.HomePriorWeekPitches);
        if (away is null || home is null) return null;

        return (meta ?? new MatchReplayRecord()) with
        {
            Id = MatchReplayRecord.IdFor(awaySchoolId, homeSchoolId, inputs.RngState),
            EngineVersion = MatchReplayVersion.Current,
            Reasons = reasons,
            AwaySchoolId = awaySchoolId, HomeSchoolId = homeSchoolId,
            AwayName = result.AwayName, HomeName = result.HomeName,
            AwayRuns = result.AwayRuns, HomeRuns = result.HomeRuns, MercyEnded = result.MercyEnded,
            RngState = inputs.RngState,
            Away = away, Home = home,
            Context = ReplayContextSpec.From(inputs.Ctx),
            Decisions = inputs.Decisions,
        };
    }

    /// <summary>
    /// 裏試合（<see cref="BackgroundMatchResolver"/>）の記録。両校とも敵AIの采配（学校の三層プロファイル）で解いた試合。
    /// </summary>
    internal static MatchReplayRecord? ForBackground(
        School away, School home, Team awayTeam, Team homeTeam, GameContext ctx, ulong[] rngState,
        IReadOnlyDictionary<Players.Player, double>? awayRecovery, IReadOnlyDictionary<Players.Player, double>? homeRecovery,
        GameResult result, NotableReason reasons, TournamentMatchContext? context)
    {
        var inputs = new MatchReplayInputs(
            awayTeam, homeTeam, ctx, rngState, awayRecovery, homeRecovery, null, null, Array.Empty<GameDecision>());
        var meta = new MatchReplayRecord
        {
            MatchDay = context?.MatchDay ?? -1,
            RoundsRemaining = context?.RoundsRemaining ?? -1,
        };
        return Create(inputs, BrainOf(awayTeam, away), BrainOf(homeTeam, home), result, reasons, away.Id, home.Id, meta);
    }

    private static ReplayBrainSpec? BrainOf(Team team, School school)
        => team.Tactics is Match.Tactics.AiTacticsBrain ? ReplayBrainSpec.FromSchool(school) : null;
}

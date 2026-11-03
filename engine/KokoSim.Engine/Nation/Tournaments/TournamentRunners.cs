using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>保存された進行体を型ごとに組み直す入口（issue #772）。状態にリーグ戦が載っていればリーグ戦、無ければ勝ち抜き戦。</summary>
public static class TournamentRunners
{
    public static ITournamentRunner FromSaved(
        TournamentRunnerState state, IReadOnlyList<School> field, School manager,
        NationCoefficients coeff, TournamentSchedule schedule,
        IPlayerMatchResolver? playerResolver = null, IBackgroundMatchResolver? backgroundResolver = null,
        Action<int, int, GameResult>? onBackgroundMatch = null)
        => state.League is not null
            ? BlockLeagueRunner.FromSaved(state, field, manager, coeff, schedule, playerResolver, backgroundResolver, onBackgroundMatch)
            : TournamentRunner.FromSaved(state, field, manager, coeff, schedule, playerResolver, backgroundResolver, onBackgroundMatch);
}

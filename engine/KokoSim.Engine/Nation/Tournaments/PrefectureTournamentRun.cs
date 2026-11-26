using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>夏（<see cref="NationalTournamentEngine"/>）と秋（<see cref="AutumnNationEngine"/>）の県大会が共有する、1県ぶんの観戦消化。</summary>
internal static class PrefectureTournamentRun
{
    /// <summary>
    /// 自校非関与の1県を観戦扱いで全カード裏試合として解き、最終のブラケット概要（全カード・優勝校名）を返す。
    /// <paramref name="prefRng"/> は呼び出し側が基準乱数から Fork 済みのもの。
    /// <paramref name="resolution"/>＝ChampionsOnly は集計モデル（backgroundResolver=null＝1球シムも成績畳み込みもしない軽量経路）。
    /// </summary>
    public static TournamentBracketView Run(
        IReadOnlyList<School> field, string title, IRandomSource prefRng,
        NationRosters rosters, GameContext ctx, NationCoefficients coeff, TournamentSchedule schedule,
        int yearIndex, NationTournamentStats stats, ModernRules? modernRules, int? calendarYear,
        IEnemyBrainFactory? brains, Action? afterMatch, SummerResolution resolution,
        bool useRegionalStadiumTier, bool summerStadiums, bool thirdPlaceMatch)
    {
        IBackgroundMatchResolver? bg = resolution == SummerResolution.ChampionsOnly
            ? null
            : new BackgroundMatchResolver(rosters, ctx, yearIndex, stats, modernRules, calendarYear, brains,
                afterMatch: afterMatch, useRegionalStadiumTier: useRegionalStadiumTier, summerStadiums: summerStadiums);
        // 自校非関与＝観戦扱い。nominal manager に field[0] を渡すと球数台帳がその1校だけ自校キーで記録され、
        // 読み手（裏試合の回復・エース温存）が引けなくなる。
        var runner = TournamentRunner.Spectated(
            field, coeff, prefRng, schedule, title, backgroundResolver: bg, thirdPlaceMatch: thirdPlaceMatch);

        // 敗退直後は残りブラケットが未消化（#529 のスライス駆動化）。この呼び出し自体が既に1県丸ごとの
        // 背景ジョブ上なので、同期に最後まで消化してよい。
        runner.DrainBracketResolution();
        return runner.BuildBracketView();
    }
}

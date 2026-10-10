using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 1県の秋季県大会の結果（設計書05 §1.5b）。<see cref="PrefectureResult"/>（夏）と違い、
/// <b>優勝校だけでなく順位付きプレースメント</b>を持つ＝秋季地区大会の出場枠
/// （<see cref="RegionalFormat.BerthsFor"/>）は「県大会で何位だったか」を要るため。
/// </summary>
public sealed record AutumnPrefectureResult(
    int PrefectureId, string PrefectureName, string RegionRomaji,
    IReadOnlyList<int> PlacementIds, int EntrantCount)
{
    /// <summary>代表校なし（1校未満）。</summary>
    public bool HasChampion => PlacementIds.Count > 0;
    public int ChampionId => PlacementIds.Count > 0 ? PlacementIds[0] : PrefectureResult.NoChampion;
    /// <summary>上位 n 校の校ID（地区大会の出場枠カット）。</summary>
    public IReadOnlyList<int> Top(int n) => PlacementIds.Take(Math.Max(0, n)).ToList();
}

/// <summary>再開可能な秋季全国裏試合（<see cref="NationalTournamentEngine.SummerRun"/> の秋版）。</summary>
public sealed record AutumnRun(int Total, IReadOnlyList<Func<AutumnPrefectureResult>> Jobs);

/// <summary>
/// 秋季県大会47県ぶんを一括/スライスで解決するオーケストレータ（設計書05 §1.5b）。
/// <see cref="NationalTournamentEngine"/>（夏・49区画・優勝校のみ）と対になる秋版で、夏と違い
/// 北海道・東京も分割しない（47都道府県そのまま）代わりに<b>順位付きプレースメント</b>を返す
/// （地区大会の出場枠は県内順位で決まるため）。自校が対話的に消化する県は
/// <paramref name="excludePrefectureId"/> で除外する（Shell が別 TournamentRunner で回す）。
/// </summary>
public static class AutumnNationEngine
{
    /// <summary>一括版（テスト用）。<see cref="BeginAutumn"/> のジョブを順に全実行するのと等価。</summary>
    public static IReadOnlyList<AutumnPrefectureResult> RunAutumn(
        Nation nation, NationRosters rosters, GameContext ctx, NationCoefficients coeff,
        TournamentSchedule schedule, int yearIndex, NationTournamentStats stats, IRandomSource rng,
        int? excludePrefectureId = null, ModernRules? modernRules = null, int? calendarYear = null,
        IEnemyBrainFactory? brains = null, SummerResolution resolution = SummerResolution.Full,
        bool useRegionalStadiumTier = false)
        => BeginAutumn(nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
            excludePrefectureId, modernRules, calendarYear, brains, resolution: resolution,
            useRegionalStadiumTier: useRegionalStadiumTier)
            .Jobs.Select(j => j()).ToList();

    /// <summary>秋季県大会47県ぶんを「県ごとの独立ジョブ列」として構築する（#208 と同型のスライス化）。
    /// <paramref name="useRegionalStadiumTier"/> は夏（<see cref="NationalTournamentEngine.BeginSummer"/>）と同じ意味（issue #636）。</summary>
    public static AutumnRun BeginAutumn(
        Nation nation, NationRosters rosters, GameContext ctx, NationCoefficients coeff,
        TournamentSchedule schedule, int yearIndex, NationTournamentStats stats, IRandomSource rng,
        int? excludePrefectureId = null, ModernRules? modernRules = null, int? calendarYear = null,
        IEnemyBrainFactory? brains = null, Action? afterMatch = null,
        SummerResolution resolution = SummerResolution.Full, bool useRegionalStadiumTier = false)
    {
        var jobs = nation.Prefectures
            .Where(p => excludePrefectureId is not int ex || p.Id != ex)
            .Select(pref => (Func<AutumnPrefectureResult>)(() => RunPrefecture(
                nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
                pref, modernRules, calendarYear, brains, afterMatch, resolution, useRegionalStadiumTier)))
            .ToList();
        return new AutumnRun(jobs.Count, jobs);
    }

    private static AutumnPrefectureResult RunPrefecture(
        Nation nation, NationRosters rosters, GameContext ctx, NationCoefficients coeff,
        TournamentSchedule schedule, int yearIndex, NationTournamentStats stats, IRandomSource rng,
        Prefecture pref, ModernRules? modernRules, int? calendarYear, IEnemyBrainFactory? brains,
        Action? afterMatch, SummerResolution resolution, bool useRegionalStadiumTier)
    {
        var field = nation.InPrefecture(pref.Id).ToList();
        var regionRomaji = PrefectureNames.RegionOf(pref.Id);
        if (field.Count < 2)
        {
            var sole = field.FirstOrDefault();
            var solePlacement = sole is null ? Array.Empty<int>() : new[] { sole.Id };
            return new AutumnPrefectureResult(pref.Id, pref.Name, regionRomaji, solePlacement, field.Count);
        }

        IBackgroundMatchResolver? bg = resolution == SummerResolution.ChampionsOnly
            ? null
            : new BackgroundMatchResolver(rosters, ctx, yearIndex, stats, modernRules, calendarYear, brains,
                afterMatch: afterMatch, useRegionalStadiumTier: useRegionalStadiumTier);
        var prefRng = rng.Fork(0xACE0_0000UL ^ (uint)pref.Id);
        var runner = new TournamentRunner(
            field, field[0], coeff, prefRng, schedule, $"{pref.Name}大会",
            playerResolver: null, backgroundResolver: bg);

        while (!runner.Finished) runner.PlayNextPlayerMatch();
        runner.DrainBracketResolution();
        var matches = runner.BuildBracketView().Matches;
        var byId = field.ToDictionary(s => s.Id);
        var placement = KnockoutPlacement.FromMatches(matches, id => byId[id]);
        return new AutumnPrefectureResult(
            pref.Id, pref.Name, regionRomaji, placement.Select(s => s.Id).ToList(), field.Count);
    }
}

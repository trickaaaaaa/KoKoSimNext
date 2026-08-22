using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 秋季地区大会1地区の結果（設計書05 §1.5b）。<see cref="RegionalNationEngine.RunOtherRegions"/> の出力。
/// <see cref="PlacementIds"/> は順位付き（先頭が地区王者）＝センバツ選考（<see cref="SenbatsuSelection.Select"/>）
/// の入力そのもの。
/// </summary>
public sealed record RegionalResult(string Region, IReadOnlyList<int> PlacementIds, bool ChampionToJingu)
{
    public bool HasChampion => PlacementIds.Count > 0;
    public int ChampionId => PlacementIds.Count > 0 ? PlacementIds[0] : PrefectureResult.NoChampion;
}

/// <summary>
/// 秋季地区大会（<see cref="AutumnFlowEngine"/> 手順2 と同じロジック）を、自地区を除いた他地区ぶんだけ
/// 計算する（設計書05 §1.5b）。自地区（自校が関与する可能性がある地区）は Shell 側が
/// <see cref="TournamentRunner"/>（対話 or 観戦）で別途解決するため、ここでは扱わない。
///
/// <para>入力は「県ごとの秋季県大会プレースメント（校ID順位列）」＝自県は対話プレイ済みの結果
/// （<c>GameSession.LastAutumnPlacementIds</c>）、他県は背景シム（<c>AutumnBackgroundSim</c>）の結果を
/// Shell 側で1つの辞書にまとめて渡す。エンジンは Unity 非依存なのでこの境界を意識しない。</para>
/// </summary>
public static class RegionalNationEngine
{
    /// <summary>
    /// 自地区（<paramref name="excludeRegion"/>）以外の地区大会を解決する。県の所属地区は
    /// <paramref name="regionOf"/>（<see cref="PrefectureNames.RegionOf"/> を渡す想定）で引く。
    /// </summary>
    public static IReadOnlyList<RegionalResult> RunOtherRegions(
        RegionalFormatSet formats,
        IReadOnlyDictionary<int, IReadOnlyList<int>> prefecturePlacements,
        Func<int, string> regionOf,
        Func<int, School> byId,
        NationCoefficients coeff,
        IRandomSource rng,
        int calendarYear,
        string? excludeRegion = null)
    {
        var byRegion = prefecturePlacements.Keys
            .GroupBy(regionOf)
            .ToDictionary(g => g.Key, g => g.ToList());

        var results = new List<RegionalResult>();
        foreach (var fmt in formats.Regions)
        {
            if (fmt.Region == excludeRegion) continue;
            if (!byRegion.TryGetValue(fmt.Region, out var prefIds)) continue;

            IReadOnlyList<int> placement;
            if (fmt.SinglePrefecture)
            {
                // 都大会・全道大会が地区大会を兼任＝県大会の順位をそのまま地区順位とする。
                placement = prefIds.Count > 0 && prefecturePlacements.TryGetValue(prefIds[0], out var sole)
                    ? sole : Array.Empty<int>();
            }
            else
            {
                var entrants = new List<School>();
                foreach (var p in prefIds.OrderBy(x => x))
                {
                    if (!prefecturePlacements.TryGetValue(p, out var ph) || ph.Count == 0) continue;
                    var berths = Math.Max(1, fmt.BerthsFor(PrefectureNames.JisOrder[p], calendarYear));
                    foreach (var id in ph.Take(berths)) entrants.Add(byId(id));
                }
                if (entrants.Count == 0) { placement = Array.Empty<int>(); }
                else
                {
                    var regionRng = rng.Fork(0xB10C_0000UL ^ (uint)StableHash(fmt.Region));
                    var knock = Knockout.Run(entrants, coeff, regionRng);
                    placement = knock.Placement.Select(s => s.Id).ToList();
                }
            }
            results.Add(new RegionalResult(fmt.Region, placement, fmt.ChampionToJingu));
        }
        return results;
    }

    /// <summary>地区名（ローマ字の固定語彙）からForkキーを安定して導く。文字コード順の和なので実行間で不変。</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }
}

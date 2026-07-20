using KokoSim.Engine.Core;
using NationState = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>1地区大会の結果。優勝校＝明治神宮大会へ進出、順位＝センバツ選考の材料（設計書05 §1.5/§4）。</summary>
public sealed record RegionResult(
    string Region, School Champion, IReadOnlyList<School> Placement, bool ToJingu);

/// <summary>秋季県大会 → 地区大会 → 明治神宮 → センバツ選考の1年分の結果（設計書05 §1.5/§4）。</summary>
public sealed record AutumnFlowResult(
    int Year,
    IReadOnlyDictionary<int, PrefTournamentResult> PrefResults,
    IReadOnlyList<RegionResult> Regions,
    IReadOnlyList<School> JinguField,
    School JinguChampion,
    string JinguChampionRegion,
    IReadOnlyList<School> SenbatsuField);

/// <summary>
/// 秋の大会フロー（設計書05 §1.5/§4）を1年分回すオーケストレータ。
/// 秋季県大会（県ごとの PrefFormat）→ 各県の出場枠を地区大会へ集約（RegionalFormat.BerthsFor）→
/// 地区王者10校で明治神宮大会 → 前年秋の地区順位＋神宮枠でセンバツ選考。
/// 部品（PrefTournamentEngine/Knockout/SenbatsuSelection）を接続するだけで、判定は各部品が担う。
/// 県Id順→地区（regionals順）→神宮 の順に単一乱数を流して決定論。純追加＝既存ループの挙動は不変。
/// </summary>
public static class AutumnFlowEngine
{
    // 専用フォーマットの無い県の既定形式＝県一発トーナメント（設計書05 §1.5, 構造の中立既定）。
    private static readonly PrefFormat DefaultKnockout = new()
    {
        DistrictAssignment = DistrictAssignment.None,
        Stages = new[] { new StageFormat { Name = "県大会", Type = StageType.Knockout } },
    };

    public static AutumnFlowResult Run(
        NationState nation,
        PrefectureTable prefTable,
        RegionalFormatSet regionals,
        SenbatsuBerths senbatsuBerths,
        NationCoefficients coeff,
        int year,
        IRandomSource rng,
        IReadOnlyDictionary<string, PrefFormat>? prefFormats = null,
        IReadOnlyDictionary<int, School>? recommended = null)
    {
        prefFormats ??= new Dictionary<string, PrefFormat>();

        // 1) 秋季県大会。県ごとにフォーマットを解決（専用YAML→無ければ既定knockout）して実行。県Id順で決定論。
        var prefResults = new Dictionary<int, PrefTournamentResult>();
        foreach (var pref in nation.Prefectures.OrderBy(p => p.Id))
        {
            var info = prefTable.ById(pref.Id);
            if (info is null) continue;
            var entrants = nation.InPrefecture(pref.Id).ToList();
            if (entrants.Count == 0) continue;

            var fmt = prefFormats.TryGetValue(info.Name, out var f) ? f : DefaultKnockout;
            School? rec = null;
            if (recommended is not null) recommended.TryGetValue(pref.Id, out rec);
            prefResults[pref.Id] = PrefTournamentEngine.Run(fmt, entrants, coeff, rng, rec);
        }

        // 2) 地区大会。県の出場枠（BerthsFor）を集約して地区王者と順位を決める。regionals の並びで決定論。
        var regionResults = new List<RegionResult>();
        var senbatsuPlacements = new Dictionary<string, IReadOnlyList<School>>();
        foreach (var rf in regionals.Regions)
        {
            var prefs = prefTable.InRegion(rf.Region);
            if (prefs.Count == 0) continue;

            IReadOnlyList<School> placement;
            School champion;
            if (rf.SinglePrefecture)
            {
                // 都大会・全道大会が地区大会を兼任（設計書05 §1.5）。県大会の順位をそのまま地区順位とする。
                var p = prefs[0];
                if (!prefResults.TryGetValue(p.Id, out var pr)) continue;
                placement = pr.FinalPlacement;
                champion = pr.Champion;
            }
            else
            {
                var entrants = new List<School>();
                foreach (var p in prefs)
                {
                    if (!prefResults.TryGetValue(p.Id, out var pr)) continue;
                    var berths = System.Math.Max(1, rf.BerthsFor(p.Name, year));
                    entrants.AddRange(pr.FinalPlacement.Take(berths));
                }
                if (entrants.Count == 0) continue;
                var knock = Knockout.Run(entrants, coeff, rng);
                placement = knock.Placement;
                champion = knock.Champion;
            }

            regionResults.Add(new RegionResult(rf.Region, champion, placement, rf.ChampionToJingu));
            senbatsuPlacements[rf.Region] = placement;
        }

        // 3) 明治神宮大会（高校の部）。神宮動線を持つ地区の王者10校のトーナメント。
        var jinguField = regionResults.Where(r => r.ToJingu).Select(r => r.Champion).ToList();
        if (jinguField.Count == 0)
            throw new System.InvalidOperationException("明治神宮大会の出場校がありません（地区大会が成立していない）。");
        var jinguChampion = Knockout.Run(jinguField, coeff, rng).Champion;
        var jinguChampionRegion = regionResults.First(r => r.Champion.Id == jinguChampion.Id).Region;

        // 4) センバツ選考。地区別一般枠＋神宮優勝地区の+1枠（21世紀枠は非採用＝成績ベース, 設計書05 §4）。
        var senbatsu = SenbatsuSelection.Select(senbatsuPlacements, senbatsuBerths, jinguChampionRegion);

        return new AutumnFlowResult(
            year, prefResults, regionResults, jinguField, jinguChampion, jinguChampionRegion, senbatsu);
    }
}

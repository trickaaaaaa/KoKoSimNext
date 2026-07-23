using KokoSim.Engine.Core;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Engine.Nation;

/// <summary>秋の大会フローの要約（明治神宮優勝校＋翌春センバツ出場校, 設計書05 §1.5/§4）。</summary>
public sealed record AutumnSummary(
    School JinguChampion, string JinguChampionRegion, IReadOnlyList<School> Senbatsu);

/// <summary>1年の全国結果スナップショット。夏の甲子園＋（有効時）秋の大会フロー。</summary>
public sealed record NationYear(
    int Year,
    int ChampionId,
    string ChampionName,
    int ChampionPrefecture,
    double ChampionStrength,
    IReadOnlyDictionary<Tier, int> TierCounts,
    double AverageStrength,
    int TierChangesFromLastYear,
    AutumnSummary? Autumn = null);

/// <summary>複数年の全国記録。</summary>
public sealed record NationHistory(IReadOnlyList<NationYear> Years, Nation FinalNation);

/// <summary>
/// 全国運営エンジン（設計書05）。毎年: 夏の地方大会（49地方） → 甲子園 → AI校進化。
/// DoD: 10年で勢力図が変動しつつ全国統計が安定。決定論。
/// </summary>
public static class NationEngine
{
    /// <summary>
    /// 全国を years 年運営する。prefTable と regionals を渡すと秋の大会フロー（秋季→地区→神宮→センバツ）を
    /// 毎年併走させる。秋フローは独立ストリーム(Fork)で回すので、夏の甲子園・AI校進化の乱数列は不変
    /// ＝秋を有効にしても既存の全国統計（優勝列・平均強さ）は1ビットも変わらない（既定オフ＝帯不変）。
    /// startYear は暦年（近畿の隔年制・開催県ローテ・現代ルールの年代連動に使う, 既定2025）。
    /// </summary>
    public static NationHistory Run(
        int years, SchoolNameVocab vocab, NationCoefficients coeff, IRandomSource rng,
        PrefectureTable? prefTable = null,
        RegionalFormatSet? regionals = null,
        SenbatsuBerths? senbatsuBerths = null,
        IReadOnlyDictionary<string, PrefFormat>? prefFormats = null,
        int startYear = 2025)
    {
        var autumnEnabled = prefTable is not null && regionals is not null;
        var senbatsu = senbatsuBerths ?? new SenbatsuBerths();

        // 地理固定割の県は学校へ県内地区を付与（geographic な group_split が実際に地区で割れる）。
        var districtPlan = prefTable is not null && prefFormats is not null
            ? DistrictPlan.Build(prefTable, prefFormats)
            : null;
        var nation = NationGenerator.Generate(vocab, coeff, rng, districtPlan);
        var snapshots = new List<NationYear>(years);
        var lastTier = SnapshotTiers(nation);

        for (var year = 1; year <= years; year++)
        {
            var totalWins = new Dictionary<int, int>();

            // 夏の地方大会（49地方=47県のうち北海道・東京だけ2分割, 設計書05 §1.1 / issue #65）→ 各代表。
            var reps = new List<School>(49);
            foreach (var region in SummerRegions.Build(nation.Prefectures))
            {
                var entrants = SummerRegions.Entrants(nation, region).ToList();
                if (entrants.Count == 0) continue;
                var result = TournamentEngine.Run(entrants, coeff, rng);
                Accumulate(totalWins, result.WinsBySchool);
                reps.Add(result.Champion);
            }

            // 甲子園（49代表）。
            var koshien = TournamentEngine.Run(reps, coeff, rng);
            Accumulate(totalWins, koshien.WinsBySchool);
            var champion = koshien.Champion;

            // 秋の大会フロー（設計書05 §1.5/§4）。独立ストリーム(Fork)＝夏・AI進化の乱数列を乱さない。
            AutumnSummary? autumn = null;
            if (autumnEnabled)
            {
                var calYear = startYear + (year - 1);
                var af = AutumnFlowEngine.Run(
                    nation, prefTable!, regionals!, senbatsu, coeff, calYear,
                    rng.Fork(0xAB77_0000UL ^ (ulong)year), prefFormats);
                autumn = new AutumnSummary(af.JinguChampion, af.JinguChampionRegion, af.SenbatsuField);
            }

            // 記録（進化前の当年の姿）。
            var tierCounts = SnapshotTierCounts(nation);
            var avgStrength = nation.Schools.Average(s => s.Strength);
            var currentTier = SnapshotTiers(nation);
            var tierChanges = CountTierChanges(lastTier, currentTier);
            lastTier = currentTier;

            snapshots.Add(new NationYear(
                year, champion.Id, champion.Name, champion.PrefectureId, champion.Strength,
                tierCounts, avgStrength, tierChanges, autumn));

            // AI校進化。
            AiSchoolModel.Evolve(nation, totalWins, champion.Id, coeff, rng);
        }

        return new NationHistory(snapshots, nation);
    }

    private static void Accumulate(Dictionary<int, int> total, IReadOnlyDictionary<int, int> add)
    {
        foreach (var kv in add)
        {
            total[kv.Key] = (total.TryGetValue(kv.Key, out var v) ? v : 0) + kv.Value;
        }
    }

    private static Dictionary<int, Tier> SnapshotTiers(Nation nation)
    {
        var map = new Dictionary<int, Tier>(nation.Schools.Count);
        foreach (var s in nation.Schools) map[s.Id] = s.Tier;
        return map;
    }

    private static int CountTierChanges(Dictionary<int, Tier> before, Dictionary<int, Tier> after)
    {
        var changes = 0;
        foreach (var kv in after)
        {
            if (before.TryGetValue(kv.Key, out var t) && t != kv.Value) changes++;
        }
        return changes;
    }

    private static Dictionary<Tier, int> SnapshotTierCounts(Nation nation)
    {
        var counts = new Dictionary<Tier, int>();
        foreach (Tier t in Enum.GetValues(typeof(Tier))) counts[t] = 0;
        foreach (var s in nation.Schools) counts[s.Tier]++;
        return counts;
    }
}

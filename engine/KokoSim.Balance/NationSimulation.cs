using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Balance;

/// <summary>全国10年シミュレーションと勢力図変動・全国統計の集計（Phase 4 DoD）。</summary>
public static class NationSimulation
{
    public static NationHistory Run(
        int years, ulong seed, string? schoolNamesPath, string? coefficientsPath = null,
        string? autumnDataDir = null, int startYear = 2025)
    {
        var vocab = schoolNamesPath is not null
            ? SchoolNamesLoader.LoadFromFile(schoolNamesPath)
            : new SchoolNameVocab();
        var coeff = coefficientsPath is not null
            ? CoefficientsLoader.LoadFromFile(coefficientsPath).Nation
            : new NationCoefficients();

        // --autumn <dataDir> が指定されたら秋の大会フロー（秋季→地区→神宮→センバツ）を併走させる。
        if (autumnDataDir is not null)
        {
            var (prefTable, regionals, prefFormats) = LoadAutumnData(autumnDataDir);
            return NationEngine.Run(
                years, vocab, coeff, new Xoshiro256Random(seed),
                prefTable, regionals, null, prefFormats, startYear);
        }
        return NationEngine.Run(years, vocab, coeff, new Xoshiro256Random(seed));
    }

    /// <summary>data ディレクトリから 47県対応表・地区枠・県別フォーマットを読み込む（IOはConfig層に隔離）。</summary>
    internal static (PrefectureTable, RegionalFormatSet, IReadOnlyDictionary<string, PrefFormat>) LoadAutumnData(string dataDir)
    {
        var prefTable = PrefectureTableLoader.LoadFromFile(Path.Combine(dataDir, "prefectures.yaml"));
        var pfDir = Path.Combine(dataDir, "pref-formats");
        var regionals = RegionalFormatLoader.LoadFromFile(Path.Combine(pfDir, "regional-tournaments.yaml"));

        var prefFormats = new Dictionary<string, PrefFormat>();
        foreach (var path in Directory.EnumerateFiles(pfDir, "*.yaml"))
        {
            if (Path.GetFileName(path) == "regional-tournaments.yaml") continue;
            var fmt = PrefFormatLoader.LoadFromFile(path);
            if (!string.IsNullOrEmpty(fmt.Pref)) prefFormats[fmt.Pref] = fmt;
        }
        return (prefTable, regionals, prefFormats);
    }

    public static string Report(NationHistory h, ulong seed)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine("# KokoSim 全国エンジン 統計レポート（Phase 4）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {seed} / 年数: {h.Years.Count} / 総校数: {h.FinalNation.Schools.Count}");
        sb.AppendLine();

        // 勢力図変動: 優勝校が毎年変わるか、県が散るか。
        var distinctChampions = h.Years.Select(y => y.ChampionId).Distinct().Count();
        var distinctPrefs = h.Years.Select(y => y.ChampionPrefecture).Distinct().Count();
        var avgTierChanges = h.Years.Skip(1).Average(y => (double)y.TierChangesFromLastYear);
        sb.AppendLine(c, $"- 異なる優勝校数: {distinctChampions}/{h.Years.Count}（勢力図変動）");
        sb.AppendLine(c, $"- 優勝県のばらつき: {distinctPrefs}県");
        sb.AppendLine(c, $"- 年間ティア変動校数(平均): {avgTierChanges:F0}");
        sb.AppendLine();

        sb.AppendLine("| 年 | 優勝校 | 県 | 強さ | 平均強さ | S | A | B | ティア変動 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var y in h.Years)
        {
            sb.AppendLine(c,
                $"| {y.Year} | {y.ChampionName} | {y.ChampionPrefecture + 1} | {y.ChampionStrength:F0} | " +
                $"{y.AverageStrength:F1} | {y.TierCounts[Tier.S]} | {y.TierCounts[Tier.A]} | {y.TierCounts[Tier.B]} | " +
                $"{y.TierChangesFromLastYear} |");
        }

        // 秋の大会フロー（--autumn 有効時のみ）。神宮優勝校・地区・センバツ出場校数。
        if (h.Years.Count > 0 && h.Years[0].Autumn is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## 秋の大会フロー（秋季→地区→明治神宮→センバツ選考）");
            sb.AppendLine();
            sb.AppendLine("| 年 | 神宮優勝校 | 神宮優勝地区 | センバツ出場校数 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var y in h.Years)
            {
                if (y.Autumn is not { } a) continue;
                // 校名は県内ユニークだが県を跨ぐと同名が有り得る（現実同様）。神宮優勝校は県を併記して別校を区別する。
                sb.AppendLine(c,
                    $"| {y.Year} | {a.JinguChampion.Name}（県{a.JinguChampion.PrefectureId + 1}） | {a.JinguChampionRegion} | {a.Senbatsu.Count} |");
            }
        }
        return sb.ToString();
    }
}

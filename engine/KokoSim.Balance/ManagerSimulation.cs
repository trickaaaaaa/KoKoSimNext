using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;

namespace KokoSim.Balance;

/// <summary>監督キャリアN年を回し「修行編→フリー化」の再現を集計（Phase 5 DoD）。</summary>
public static class ManagerSimulation
{
    public static (CareerTimeline Timeline, Manager Manager) Run(
        int years, ulong seed, string? coefficientsPath, string? schoolNamesPath,
        string? autumnDataDir = null, int startYear = 2025)
    {
        var vocab = schoolNamesPath is not null
            ? SchoolNamesLoader.LoadFromFile(schoolNamesPath)
            : new SchoolNameVocab();

        var nationCoeff = new NationCoefficients();
        var careerCoeff = new CareerCoefficients();
        if (coefficientsPath is not null)
        {
            var b = CoefficientsLoader.LoadFromFile(coefficientsPath);
            nationCoeff = b.Nation;
            careerCoeff = b.Career;
        }

        var manager = new Manager { Name = "プレイヤー監督" };

        CareerTimeline timeline;
        if (autumnDataDir is not null)
        {
            var (prefTable, regionals, prefFormats) = NationSimulation.LoadAutumnData(autumnDataDir);
            timeline = CareerEngine.Run(
                years, manager, vocab, nationCoeff, careerCoeff, new Xoshiro256Random(seed),
                growthCoeff: null, prefTable: prefTable, regionals: regionals,
                senbatsuBerths: null, prefFormats: prefFormats, startYear: startYear);
        }
        else
        {
            timeline = CareerEngine.Run(years, manager, vocab, nationCoeff, careerCoeff, new Xoshiro256Random(seed));
        }
        return (timeline, manager);
    }

    public static string Report((CareerTimeline Timeline, Manager Manager) result, ulong seed, bool autumn = false)
    {
        var (t, m) = result;
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine("# KokoSim 監督キャリア レポート（Phase 5）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {seed} / 年数: {t.Years.Count}");
        sb.AppendLine(c, $"- 赴任校数: {t.SchoolsServed} / フリー化年: {(t.YearBecameFree?.ToString() ?? "未達")}");
        sb.AppendLine(c, $"- 甲子園出場: {t.KoshienAppearances}回 / 全国制覇: {t.NationalTitles}回");
        if (autumn) sb.AppendLine(c, $"- センバツ出場: {t.SenbatsuAppearances}回（秋の大会フロー）");
        sb.AppendLine(c, $"- 最終指導力(平均): {m.AverageCoaching:F1} / 名声: {m.Fame:F1} / 資金: {m.Funds:F0}万");
        sb.AppendLine();
        var senbatsuHead = autumn ? " センバツ |" : "";
        var senbatsuSep = autumn ? "---|" : "";
        sb.AppendLine(c, $"| 年 | 県 | 身分 | 指導力 | 名声 | 信頼 | 甲子園 |{senbatsuHead} 勝 | 転任 |");
        sb.AppendLine(c, $"|---|---|---|---|---|---|---|{senbatsuSep}---|---|");
        foreach (var y in t.Years)
        {
            var status = y.Status == ManagerStatus.Teacher ? "教員" : "フリー";
            var koshien = y.NationalChampion ? "優勝" : y.ReachedKoshien ? "出場" : "-";
            var transfer = y.Transferred ? "→" : "";
            var senbatsu = autumn
                ? $" {(y.ReachedJingu ? "神宮" : y.ReachedSenbatsu ? "出場" : "-")} |"
                : "";
            sb.AppendLine(c,
                $"| {y.Year} | {y.Prefecture + 1} | {status} | {y.AverageCoaching:F0} | {y.Fame:F0} | " +
                $"{y.Trust:F0} | {koshien} |{senbatsu} {y.Wins} | {transfer} |");
        }
        return sb.ToString();
    }
}

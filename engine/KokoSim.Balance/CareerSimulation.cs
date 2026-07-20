using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;

namespace KokoSim.Balance;

/// <summary>N年キャリアのシーズンループを回し、育成曲線・イベント発火を集計（Phase 3 DoD）。</summary>
public static class CareerSimulation
{
    public static CareerSummary Run(int years, ulong seed, string? coefficientsPath, string? eventsPath)
    {
        var ctx = BuildContext(coefficientsPath, eventsPath);
        return SeasonEngine.Run(years, ctx, new Xoshiro256Random(seed));
    }

    private static SeasonContext BuildContext(string? coefficientsPath, string? eventsPath)
    {
        var ctx = new SeasonContext();
        if (coefficientsPath is not null)
        {
            var b = CoefficientsLoader.LoadFromFile(coefficientsPath);
            ctx = ctx with { Training = b.Training, Roster = b.Roster, Personalities = b.Personalities };
        }
        if (eventsPath is not null)
        {
            ctx = ctx with { Events = EventsLoader.LoadFromFile(eventsPath) };
        }
        return ctx;
    }

    public static string Report(CareerSummary summary, ulong seed)
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine("# KokoSim シーズンループ 統計レポート（Phase 3）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {seed} / 年数: {summary.Years.Count}");
        sb.AppendLine(c, $"- 総イベント発火数: {summary.TotalEventsFired}");
        sb.AppendLine();
        sb.AppendLine("| 年 | 部員数 | 主力平均Lv(2-3年) | 卒業生平均Lv | イベント |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var y in summary.Years)
        {
            sb.AppendLine(c,
                $"| {y.Year} | {y.RosterCount} | {y.AvgLevelRegulars:F1} | {y.GraduatingAvgLevel:F1} | {y.EventsFired} |");
        }
        return sb.ToString();
    }
}

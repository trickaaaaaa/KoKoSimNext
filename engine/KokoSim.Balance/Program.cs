using System.Globalization;
using KokoSim.Balance;
using KokoSim.Config;
using KokoSim.Engine.Match.Field;

// KokoSim.Balance — ヘッドレス一括シミュレーションCLI。
// simulate: 1万打席の打席解決を回し、打率/三振率/四球率/本塁打率などの統計をレポート出力する（Phase 1 DoD）。

return CommandLine.Run(args);

internal static class CommandLine
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        return args[0] switch
        {
            "simulate" => RunSimulate(args[1..]),
            "simulate-games" => RunSimulateGames(args[1..]),
            "simulate-career" => RunSimulateCareer(args[1..]),
            "simulate-nation" => RunSimulateNation(args[1..]),
            "simulate-manager" => RunSimulateManager(args[1..]),
            "calibrate" => RunCalibrate(args[1..]),
            "trace" => RunTrace(args[1..]),
            "trace-diff" => RunTraceDiff(args[1..]),
            _ => Fail($"未知のコマンド: {args[0]}"),
        };
    }

    /// <summary>デバッグ観測（設計書17 §4.4）: 1球単位のトレースを JSONL で書き出す。</summary>
    private static int RunTrace(string[] args)
    {
        var o = new KokoSim.Balance.Debugging.TraceCommand.Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--games": o = o with { Games = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture) }; break;
                case "--seed": o = o with { Seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture) }; break;
                case "--out": o = o with { OutPath = RequireValue(args, ref i) }; break;
                case "--coefficients": o = o with { CoefficientsPath = RequireValue(args, ref i) }; break;
                case "--stadium": o = o with { Field = ResolveStadium(RequireValue(args, ref i)) }; break;
                case "--tactics": o = o with { UseTacticsBrain = true }; break;
                case "--only": o = o with { Only = KokoSim.Balance.Debugging.TraceCommand.ParseKinds(RequireValue(args, ref i)) }; break;
                case "--scenario": o = o with { ScenarioId = RequireValue(args, ref i) }; break;
                case "--force": o = o with { Force = RequireValue(args, ref i) }; break;
                case "--measure": o = o with { MeasureOverhead = true }; break;
                default: return Fail($"未知のオプション: {args[i]}");
            }
        }

        var summary = KokoSim.Balance.Debugging.TraceCommand.Run(o);
        Console.Write(KokoSim.Balance.Debugging.TraceCommand.Report(summary, o));
        return 0;
    }

    /// <summary>デバッグ観測（設計書17 §4.4）: 2本のトレースの「最初に食い違った球」と分布差を出す。</summary>
    private static int RunTraceDiff(string[] args)
    {
        string? a = null, b = null, reportPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--a": a = RequireValue(args, ref i); break;
                case "--b": b = RequireValue(args, ref i); break;
                case "--report": reportPath = RequireValue(args, ref i); break;
                default: return Fail($"未知のオプション: {args[i]}");
            }
        }
        if (a is null || b is null) return Fail("trace-diff には --a と --b が必要です。");

        var result = KokoSim.Balance.Debugging.TraceDiff.Compare(a, b);
        var text = KokoSim.Balance.Debugging.TraceDiff.Report(result, a, b);
        Console.Write(text);
        WriteReport(reportPath, text);
        // 食い違いがあれば非ゼロ終了（CI から「回帰した」の判定に使える）。
        return result.Identical ? 0 : 2;
    }

    private static int RunCalibrate(string[] args)
    {
        var seed = 42UL;
        string? reportPath = null;
        string? coefficientsPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed": seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture); break;
                case "--report": reportPath = RequireValue(args, ref i); break;
                case "--coefficients": coefficientsPath = RequireValue(args, ref i); break;
                default: return Fail($"未知のオプション: {args[i]}");
            }
        }
        var report = Step4Calibration.Report(seed, coefficientsPath);
        if (reportPath is not null)
        {
            System.IO.File.WriteAllText(reportPath, report);
            Console.WriteLine($"校正レポートを書き出しました: {reportPath}");
        }
        else
        {
            Console.WriteLine(report);
        }
        return 0;
    }

    private static int RunSimulateManager(string[] args)
    {
        var years = 20;
        var seed = 42UL;
        string? reportPath = null;
        string? coefficientsPath = null;
        string? schoolNamesPath = null;
        string? autumnDataDir = null;
        var startYear = 2025;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--years":
                    years = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--report":
                    reportPath = RequireValue(args, ref i);
                    break;
                case "--coefficients":
                    coefficientsPath = RequireValue(args, ref i);
                    break;
                case "--school-names":
                    schoolNamesPath = RequireValue(args, ref i);
                    break;
                case "--autumn":
                    autumnDataDir = RequireValue(args, ref i);
                    break;
                case "--start-year":
                    startYear = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                default:
                    return Fail($"未知のオプション: {args[i]}");
            }
        }

        var result = KokoSim.Balance.ManagerSimulation.Run(
            years, seed, coefficientsPath, schoolNamesPath, autumnDataDir, startYear);
        var text = KokoSim.Balance.ManagerSimulation.Report(result, seed, autumnDataDir is not null);
        Console.Write(text);
        WriteReport(reportPath, text);
        return 0;
    }

    private static int RunSimulateNation(string[] args)
    {
        var years = 10;
        var seed = 42UL;
        string? reportPath = null;
        string? schoolNamesPath = null;
        string? coefficientsPath = null;
        string? autumnDataDir = null;
        var startYear = 2025;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--years":
                    years = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--report":
                    reportPath = RequireValue(args, ref i);
                    break;
                case "--school-names":
                    schoolNamesPath = RequireValue(args, ref i);
                    break;
                case "--coefficients":
                    coefficientsPath = RequireValue(args, ref i);
                    break;
                case "--autumn":
                    autumnDataDir = RequireValue(args, ref i);
                    break;
                case "--start-year":
                    startYear = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                default:
                    return Fail($"未知のオプション: {args[i]}");
            }
        }

        var history = KokoSim.Balance.NationSimulation.Run(
            years, seed, schoolNamesPath, coefficientsPath, autumnDataDir, startYear);
        var text = KokoSim.Balance.NationSimulation.Report(history, seed);
        Console.Write(text);
        WriteReport(reportPath, text);
        return 0;
    }

    private static int RunSimulateCareer(string[] args)
    {
        var years = 10;
        var seed = 42UL;
        string? reportPath = null;
        string? coefficientsPath = null;
        string? eventsPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--years":
                    years = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--report":
                    reportPath = RequireValue(args, ref i);
                    break;
                case "--coefficients":
                    coefficientsPath = RequireValue(args, ref i);
                    break;
                case "--events":
                    eventsPath = RequireValue(args, ref i);
                    break;
                default:
                    return Fail($"未知のオプション: {args[i]}");
            }
        }

        var summary = KokoSim.Balance.CareerSimulation.Run(years, seed, coefficientsPath, eventsPath);
        var text = KokoSim.Balance.CareerSimulation.Report(summary, seed);
        Console.Write(text);
        WriteReport(reportPath, text);
        return 0;
    }

    private static int RunSimulateGames(string[] args)
    {
        var games = 10000;
        var seed = 42UL;
        string? reportPath = null;
        string? coefficientsPath = null;
        string? stadiumId = null;
        var useTacticsBrain = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--games":
                    games = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--report":
                    reportPath = RequireValue(args, ref i);
                    break;
                case "--coefficients":
                    coefficientsPath = RequireValue(args, ref i);
                    break;
                case "--stadium":
                    stadiumId = RequireValue(args, ref i);
                    break;
                case "--tactics":
                    useTacticsBrain = true;
                    break;
                default:
                    return Fail($"未知のオプション: {args[i]}");
            }
        }

        var stats = GameSimulation.Run(games, seed, coefficientsPath, ResolveStadium(stadiumId), useTacticsBrain);
        var text = GameSimulation.Report(stats, seed);
        Console.Write(text);
        WriteReport(reportPath, text);
        return 0;
    }

    private static void WriteReport(string? reportPath, string text)
    {
        if (reportPath is null) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(reportPath, text);
        Console.WriteLine($"レポートを出力しました: {reportPath}");
    }

    private static int RunSimulate(string[] args)
    {
        var atBats = 10000;
        var seed = 42UL;
        string? reportPath = null;
        string? coefficientsPath = null;
        string? stadiumId = null;
        var histogram = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--histogram":
                    histogram = true;
                    break;
                case "--at-bats":
                case "--games":
                    atBats = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--report":
                    reportPath = RequireValue(args, ref i);
                    break;
                case "--coefficients":
                    coefficientsPath = RequireValue(args, ref i);
                    break;
                case "--stadium":
                    stadiumId = RequireValue(args, ref i);
                    break;
                default:
                    return Fail($"未知のオプション: {args[i]}");
            }
        }

        var stats = AtBatSimulation.Run(atBats, seed, coefficientsPath, ResolveStadium(stadiumId), histogram);
        var text = AtBatSimulation.Report(stats, seed);
        Console.Write(text);

        if (reportPath is not null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(reportPath, text);
            Console.WriteLine($"レポートを出力しました: {reportPath}");
        }

        return 0;
    }

    /// <summary>--stadium で指定された球場idを data/stadiums.yaml から解決し FieldGeometry を返す（未指定は null＝既定球場）。</summary>
    private static FieldGeometry? ResolveStadium(string? stadiumId)
    {
        if (stadiumId is null) return null;
        var catalog = StadiumCatalogLoader.LoadCatalog(FindDataFile("stadiums.yaml"));
        if (!catalog.TryGetValue(stadiumId, out var stadium))
            throw new ArgumentException($"未知の球場id: {stadiumId}（data/stadiums.yaml）");
        return stadium.ToFieldGeometry();
    }

    private static string FindDataFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"data/{fileName} が見つかりません。");
    }

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{args[i]} には値が必要です。");
        }
        return args[++i];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("KokoSim.Balance — ヘッドレスシミュレーションCLI");
        Console.WriteLine();
        Console.WriteLine("使い方:");
        Console.WriteLine("  simulate        [--at-bats N] [--seed S] [--report path] [--coefficients data/coefficients.yaml] [--stadium ID] [--histogram]");
        Console.WriteLine("  simulate-games  [--games N]   [--seed S] [--report path] [--coefficients data/coefficients.yaml] [--tactics]");
        Console.WriteLine("                  [--tactics]                              # 両チームに StandardTacticsBrain を付与（敬遠/重盗/牽制の校正用）");
        Console.WriteLine("  simulate-career [--years N]   [--seed S] [--report path] [--coefficients data/coefficients.yaml] [--events data/events.yaml]");
        Console.WriteLine("  simulate-nation [--years N]   [--seed S] [--report path] [--school-names data/school-names.yaml]");
        Console.WriteLine("                  [--autumn data] [--start-year 2025]   # 秋の大会フロー(秋季→地区→神宮→センバツ)を併走");
        Console.WriteLine("  simulate-manager[--years N]   [--seed S] [--report path] [--coefficients data/coefficients.yaml]");
        Console.WriteLine("                  [--autumn data] [--start-year 2025]   # 監督校の秋季→センバツ経路を記録");
        Console.WriteLine();
        Console.WriteLine("  # デバッグ観測（設計書17）");
        Console.WriteLine("  trace           [--games N] [--seed S] [--out out/trace.jsonl] [--only game|pitch|pa|end]");
        Console.WriteLine("                  [--coefficients ...] [--stadium ID] [--tactics] [--scenario ID] [--force NAME] [--measure]");
        Console.WriteLine("  trace-diff      --a out/before.jsonl --b out/after.jsonl [--report out/diff.md]");
        Console.WriteLine("                  # 最初に食い違った球と分布差を出す。食い違いがあれば終了コード2");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}

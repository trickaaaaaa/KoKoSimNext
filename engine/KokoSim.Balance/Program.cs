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
            "simulate-tiers" => RunSimulateTiers(args[1..]),
            "calibrate" => RunCalibrate(args[1..]),
            "infield-timing" => RunInfieldTiming(args[1..]),
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

    /// <summary>
    /// 低ティア帯・非対称マッチの得点環境の計測（issue #536 Phase 1）。**係数も帯も動かさない**。
    /// 既存の <c>simulate-games</c> は平均的なチーム同士しか測っていないので、ティア格子と
    /// 「実プレイと同じ自校 vs 相手校」を別立てで測る。
    /// </summary>
    private static int RunSimulateTiers(string[] args)
    {
        var games = 500;
        var seed = 42UL;
        string? reportPath = null;
        string? coefficientsPath = null;
        double? slope = null;
        double? pivot = null;
        double? knee = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--games": games = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture); break;
                case "--seed": seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture); break;
                case "--report": reportPath = RequireValue(args, ref i); break;
                case "--coefficients": coefficientsPath = RequireValue(args, ref i); break;
                // ティア圧縮の掃引（issue #578 Step 1）。指定しなければ C#既定＝実ゲームと同じ値で走る。
                case "--compress-slope": slope = double.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture); break;
                case "--compress-pivot": pivot = double.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture); break;
                case "--compress-knee": knee = double.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture); break;
                default: return Fail($"未知のオプション: {args[i]}");
            }
        }
        var roster = slope is null && pivot is null && knee is null
            ? null
            : new KokoSim.Engine.Season.RosterCoefficients
            {
                TierCompressionSlope = slope ?? KokoSim.Engine.Season.RosterCoefficients.Default.TierCompressionSlope,
                TierCompressionPivot = pivot ?? KokoSim.Engine.Season.RosterCoefficients.Default.TierCompressionPivot,
                TierCompressionKnee = knee ?? KokoSim.Engine.Season.RosterCoefficients.Default.TierCompressionKnee,
            };
        var report = TierMatchupSimulation.Report(games, seed, coefficientsPath, roster);
        if (reportPath is not null)
        {
            System.IO.File.WriteAllText(reportPath, report);
            Console.WriteLine($"ティア格子レポートを書き出しました: {reportPath}");
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
        var applyWeather = false;

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
                case "--weather":
                    // 夏場の気温モデル（issue #120）。夏場帯（games_10k_summer）の実測に使う。
                    applyWeather = true;
                    break;
                default:
                    return Fail($"未知のオプション: {args[i]}");
            }
        }

        var stats = GameSimulation.Run(games, seed, coefficientsPath, ResolveStadium(stadiumId), useTacticsBrain, applyWeather);
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

    /// <summary>
    /// 内野ゴロの判定margin分布を実測する（issue #358）。帯に内野安打の指標が無いため、
    /// 守備の所要時間に関わる係数を動かす前後の比較はこのコマンドで行う。
    /// </summary>
    private static int RunInfieldTiming(string[] args)
    {
        var atBats = 40000;
        var seed = 42UL;
        string? coefficientsPath = null;
        string? stadiumId = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--at-bats":
                case "--games":
                    atBats = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    seed = ulong.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
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

        var timing = AtBatSimulation.RunInfieldTiming(atBats, seed, coefficientsPath, ResolveStadium(stadiumId));
        Console.WriteLine($"# 内野ゴロの判定margin（seed {seed}・{atBats}打席）");
        Console.WriteLine();
        Console.Write(timing.ToMarkdown());
        return 0;
    }

    private static int RunSimulate(string[] args)
    {
        var atBats = 10000;
        var seed = 42UL;
        string? reportPath = null;
        string? coefficientsPath = null;
        string? stadiumId = null;
        var histogram = false;
        var byPitcherRank = false;
        var apex = false;
        var matchupGrid = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--by-pitcher-rank":
                    byPitcherRank = true;
                    break;
                case "--matchup-grid":
                    // issue matchup-gap-saturation-k-rate: 投手レベル×打者レベルの K%/BB%/被打率 格子。
                    matchupGrid = true;
                    break;
                case "--histogram":
                    histogram = true;
                    break;
                case "--apex":
                    // issue #451: 打球の頂点/滞空/4分類の分布診断（フライがフライっぽく見えないの before/after）。
                    apex = true;
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

        var stadium = ResolveStadium(stadiumId);
        var stats = AtBatSimulation.Run(atBats, seed, coefficientsPath, stadium, histogram);
        var text = AtBatSimulation.Report(stats, seed);
        if (byPitcherRank)
        {
            // issue #402 検証: 投手ランク別の三振率（E・F投手の奪三振是正の確認）。
            var byRank = AtBatSimulation.RunByPitcherRank(atBats, seed, coefficientsPath, stadium);
            text += "\n" + AtBatSimulation.ReportByPitcherRank(byRank, seed);
        }
        if (matchupGrid)
        {
            var grid = AtBatSimulation.RunMatchupGrid(atBats, seed, coefficientsPath);
            text += $"\n## 投打差の格子（各 {atBats:N0} 打席・seed {seed}）\n\n" + grid.ToMarkdown();
        }
        if (apex)
        {
            // issue #451 検証: 打球の頂点/滞空/4分類の分布（外野フライ p50 が実物オーダー20m台に入るかの確認）。
            var profile = AtBatSimulation.RunApexProfile(atBats, seed, coefficientsPath, stadium);
            text += "\n## 打球の頂点/滞空 分布（issue #451 診断）\n\n" + profile.ToMarkdown();
        }
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
        Console.WriteLine("  simulate        [--at-bats N] [--seed S] [--report path] [--coefficients data/coefficients.yaml] [--stadium ID] [--histogram] [--apex] [--by-pitcher-rank] [--matchup-grid]");
        Console.WriteLine("  simulate-games  [--games N]   [--seed S] [--report path] [--coefficients data/coefficients.yaml] [--tactics]");
        Console.WriteLine("                  [--tactics]                              # 両チームに StandardTacticsBrain を付与（敬遠/重盗/牽制の校正用）");
        Console.WriteLine("  simulate-tiers  [--games N]   [--seed S] [--report path] [--coefficients data/coefficients.yaml] [--compress-slope X] [--compress-pivot Y]");
        Console.WriteLine("                  # ティア格子（強度30〜60の総当たり）＋実プレイの自校vs相手校＋ランク式の食い違い（issue #536・計測のみ）");
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

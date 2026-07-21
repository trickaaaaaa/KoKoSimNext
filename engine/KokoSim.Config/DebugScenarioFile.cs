using KokoSim.Engine.Debugging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// <c>data/debug/scenarios.yaml</c>（デバッグの場面ジャンプ）のローダ（設計書17 §3.4, F2）。
/// IO はこの層に隔離する（不変条件#3）。シナリオはYAMLに置き C# へハードコードしない（不変条件#4）。
///
/// <para><b>欠損は正常系</b>（OPEN-QUESTIONS Q18-2 の (4)）: リリースビルドは <c>data/debug/</c> を
/// ディレクトリ単位で同梱除外するため、ファイルが無ければ<b>0件のカタログを返し例外を投げない</b>。
/// 「壊れたYAML」だけは投げる（黙って0件にすると編集ミスに気づけないため）。</para>
/// </summary>
public static class DebugScenarioLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>data/debug/scenarios.yaml の既定の相対パス。</summary>
    public const string DefaultRelativePath = "data/debug/scenarios.yaml";

    /// <summary>ファイルから読む。存在しなければ <see cref="ScenarioCatalog.Empty"/>。</summary>
    public static ScenarioCatalog LoadFromFileOrEmpty(string? path)
    {
        if (path is null || !File.Exists(path)) return ScenarioCatalog.Empty;
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// 実行ディレクトリから上へ辿って <c>data/debug/scenarios.yaml</c> を探す（見つからなければ0件）。
    /// リポジトリ内から CLI/テストを回すときの探索経路。
    /// </summary>
    public static ScenarioCatalog LoadFromRepoOrEmpty(string? startDirectory = null)
        => LoadFromFileOrEmpty(FindDefaultPath(startDirectory));

    /// <summary>既定パスを探す（見つからなければ null）。</summary>
    public static string? FindDefaultPath(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, DefaultRelativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static ScenarioCatalog Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<FileDto>(yaml);
        if (dto?.Scenarios is null || dto.Scenarios.Count == 0) return ScenarioCatalog.Empty;

        var list = new List<ScenarioDefinition>(dto.Scenarios.Count);
        foreach (var s in dto.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(s.Id))
                throw new InvalidDataException("scenarios.yaml に id のないシナリオがあります。");
            list.Add(new ScenarioDefinition
            {
                Id = s.Id!,
                Name = s.Name ?? s.Id!,
                Away = s.Away?.School ?? "AI:tier=C",
                Home = s.Home?.School ?? "player",
                AwayScore = s.Away?.Score ?? 0,
                HomeScore = s.Home?.Score ?? 0,
                Inning = s.Inning ?? 1,
                Top = s.Top ?? true,
                Outs = s.Outs ?? 0,
                Bases = s.Bases ?? new List<int>(),
                Balls = s.Count?.Balls ?? 0,
                Strikes = s.Count?.Strikes ?? 0,
                Batter = s.Batter ?? 1,
                PitcherFatigue = s.PitcherFatigue ?? 0,
                Dh = s.ModernRules?.Dh,
                TieBreak = s.ModernRules?.Tiebreak,
                Seed = s.Seed,
                Force = s.Force,
            });
        }
        return new ScenarioCatalog(list);
    }

    private sealed class FileDto
    {
        public List<EntryDto>? Scenarios { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public SideDto? Away { get; set; }
        public SideDto? Home { get; set; }
        public int? Inning { get; set; }
        public bool? Top { get; set; }
        public int? Outs { get; set; }
        public List<int>? Bases { get; set; }
        public CountDto? Count { get; set; }
        public int? Batter { get; set; }
        public int? PitcherFatigue { get; set; }
        public ModernRulesDto? ModernRules { get; set; }
        public ulong? Seed { get; set; }
        public string? Force { get; set; }
    }

    private sealed class SideDto
    {
        public string? School { get; set; }
        public int? Score { get; set; }
    }

    private sealed class CountDto
    {
        public int? Balls { get; set; }
        public int? Strikes { get; set; }
    }

    private sealed class ModernRulesDto
    {
        public bool? Dh { get; set; }
        public bool? Tiebreak { get; set; }
    }
}

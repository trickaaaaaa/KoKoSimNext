using KokoSim.Engine.Players;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/skills.yaml（スキルカタログ）のローダ（設計書10 §5）。IO はこの層に隔離（不変条件#3）。
/// 効果量は coefficients.yaml の skills: 側。ここは id・分類・表示情報だけを読む。
/// スキルの追加は YAML 編集＋Skill enum への1行追加で完結する（不変条件#4）。
/// </summary>
public static class SkillsCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<SkillCatalogEntry> LoadFromFile(string path)
        => Parse(File.ReadAllText(path));

    public static IReadOnlyList<SkillCatalogEntry> Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<FileDto>(yaml)
            ?? throw new InvalidDataException("skills.yaml が空です。");
        var list = new List<SkillCatalogEntry>();
        foreach (var e in dto.Skills ?? new List<EntryDto>())
        {
            if (!Enum.TryParse<Skill>(e.Id, out var id))
                throw new InvalidDataException($"未知のスキルid: {e.Id}");
            var category = e.Category switch
            {
                "batting" => SkillCategory.Batting,
                "pitching" => SkillCategory.Pitching,
                "fielding" => SkillCategory.Fielding,
                "team" => SkillCategory.Team,
                "constitution" => SkillCategory.Constitution,
                "special" => SkillCategory.Special,
                _ => throw new InvalidDataException($"未知のカテゴリ: {e.Category}（{e.Id}）"),
            };
            list.Add(new SkillCatalogEntry(id, category, e.Name, e.Description, e.HiddenEligible));
        }
        return list;
    }

    private sealed class FileDto
    {
        public List<EntryDto>? Skills { get; set; }
    }

    private sealed class EntryDto
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool HiddenEligible { get; set; }
    }
}

using KokoSim.Engine.Nation.Tournaments;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/pref-formats/&lt;pref&gt;.yaml のローダ（設計書05 §1.5 / CHANGELOG 26-28）。IO はこの層に隔離（不変条件#3）。
/// 県ごとの大会形式は YAML 編集だけで差し替えられる（不変条件#4）。
/// </summary>
public static class PrefFormatLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static PrefFormat LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static PrefFormat Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<PrefDto>(yaml)
            ?? throw new InvalidDataException("pref-format YAML が空です。");
        return dto.ToModel();
    }

    private sealed class PrefDto
    {
        public string Pref { get; set; } = "";
        public int SnapshotYear { get; set; }
        public string Region { get; set; } = "";
        public List<string>? Districts { get; set; }
        public string DistrictAssignment { get; set; } = "none";
        public int RegionalBerths { get; set; } = 1;
        public bool SeedExemption { get; set; }
        public List<StageDto>? Stages { get; set; }
        public StadiumsDto? Stadiums { get; set; }

        public PrefFormat ToModel() => new()
        {
            Pref = Pref,
            SnapshotYear = SnapshotYear,
            Region = Region,
            Districts = Districts ?? new List<string>(),
            DistrictAssignment = DistrictAssignment switch
            {
                "geographic" => Engine.Nation.Tournaments.DistrictAssignment.Geographic,
                "draw" => Engine.Nation.Tournaments.DistrictAssignment.Draw,
                _ => Engine.Nation.Tournaments.DistrictAssignment.None,
            },
            RegionalBerths = RegionalBerths,
            SeedExemption = SeedExemption,
            Stages = (Stages ?? new List<StageDto>()).Select(s => s.ToModel()).ToList(),
            Stadiums = Stadiums?.ToModel() ?? StadiumPlan.None,
        };
    }

    private sealed class StadiumsDto
    {
        public string? Final { get; set; }
        public List<string>? Early { get; set; }

        public StadiumPlan ToModel() => new(Final, Early ?? new List<string>());
    }

    private sealed class StageDto
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "knockout";
        public bool ThirdPlaceMatch { get; set; }
        public int? Groups { get; set; }
        public string Grouping { get; set; } = "geographic";
        public ChildDto? Child { get; set; }
        public int? AdvancePerGroup { get; set; }
        public LoserBracketDto? LoserBracket { get; set; }
        public int? Entries { get; set; }

        public StageFormat ToModel() => new()
        {
            Name = Name,
            Type = ParseStage(Type),
            ThirdPlaceMatch = ThirdPlaceMatch,
            Groups = Groups,
            Grouping = Grouping == "draw" ? GroupingMode.Draw : GroupingMode.Geographic,
            Child = Child is null ? null : new ChildStage(ParseStage(Child.Type), Child.TeamsPerGroup),
            AdvancePerGroup = AdvancePerGroup,
            LoserBracket = LoserBracket is null ? null : new LoserBracketRule(LoserBracket.Enabled, LoserBracket.Advance),
            Entries = Entries,
        };

        private static StageType ParseStage(string t) => t switch
        {
            "round_robin" => StageType.RoundRobin,
            "group_split" => StageType.GroupSplit,
            "knockout" => StageType.Knockout,
            _ => throw new InvalidDataException($"未知のステージ型: {t}"),
        };
    }

    private sealed class ChildDto
    {
        public string Type { get; set; } = "knockout";
        public int? TeamsPerGroup { get; set; }
    }

    private sealed class LoserBracketDto
    {
        public bool Enabled { get; set; }
        public int? Advance { get; set; }
    }
}

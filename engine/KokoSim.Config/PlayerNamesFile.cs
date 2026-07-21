using KokoSim.Engine.Season;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>data/player-names.yaml のローダ（氏名語彙, 不変条件#4）。</summary>
public static class PlayerNamesLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static PlayerNameVocab LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static PlayerNameVocab Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<RootDto>(yaml) ?? new RootDto();
        var d = new PlayerNameVocab();
        return new PlayerNameVocab
        {
            FamilyNames = Convert(dto.FamilyNames) ?? d.FamilyNames,
            GivenNames = Convert(dto.GivenNames) ?? d.GivenNames,
            GivenWeightExponent = dto.GivenWeightExponent is > 0 and <= 1
                ? dto.GivenWeightExponent.Value : d.GivenWeightExponent,
        };
    }

    private static IReadOnlyList<WeightedName>? Convert(List<WeightedNameDto>? list)
    {
        if (list == null || list.Count == 0) return null;
        var result = new List<WeightedName>(list.Count);
        foreach (var e in list)
        {
            if (string.IsNullOrEmpty(e.Value)) continue;
            result.Add(new WeightedName { Value = e.Value!, Weight = e.Weight ?? 1.0 });
        }
        return result.Count == 0 ? null : result;
    }

    private sealed class RootDto
    {
        public List<WeightedNameDto>? FamilyNames { get; set; }
        public List<WeightedNameDto>? GivenNames { get; set; }
        public double? GivenWeightExponent { get; set; }
    }

    private sealed class WeightedNameDto
    {
        public string? Value { get; set; }
        public double? Weight { get; set; }
    }
}

using KokoSim.Engine.Nation.Tournaments;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/pref-formats/regional-tournaments.yaml のローダ（設計書05 §1.5 / CHANGELOG 26-27）。
/// 10地区の県別枠・開催県ボーナス・近畿隔年制・神宮動線を読み込む。IO はこの層に隔離（不変条件#3）。
/// </summary>
public static class RegionalFormatLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static RegionalFormatSet LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static RegionalFormatSet Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<FileDto>(yaml)
            ?? throw new InvalidDataException("regional-tournaments.yaml が空です。");
        var regions = (dto.Regions ?? new List<RegionDto>()).Select(r => r.ToModel()).ToList();
        return new RegionalFormatSet(regions);
    }

    private sealed class FileDto
    {
        public List<RegionDto>? Regions { get; set; }
    }

    private sealed class RegionDto
    {
        public string Region { get; set; } = "";
        public bool SinglePrefecture { get; set; }
        public bool HostPrefectureBonus { get; set; }
        public bool ChampionToJingu { get; set; } = true;
        public Dictionary<string, int>? Fixed { get; set; }
        public Dictionary<string, int>? BerthsPerPref { get; set; }
        public BiennialDto? BiennialRotation { get; set; }
        public Dictionary<int, string>? HostByYear { get; set; }

        public RegionalFormat ToModel() => new()
        {
            Region = Region,
            SinglePrefecture = SinglePrefecture,
            HostPrefectureBonus = HostPrefectureBonus,
            ChampionToJingu = ChampionToJingu,
            FixedBerths = Fixed ?? new Dictionary<string, int>(),
            BerthsPerPref = BerthsPerPref ?? new Dictionary<string, int>(),
            OddYearBerths = BiennialRotation?.OddYear,
            EvenYearBerths = BiennialRotation?.EvenYear,
            HostByYear = HostByYear ?? new Dictionary<int, string>(),
        };
    }

    private sealed class BiennialDto
    {
        public Dictionary<string, int>? OddYear { get; set; }
        public Dictionary<string, int>? EvenYear { get; set; }
    }
}

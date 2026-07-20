using KokoSim.Engine.Nation.Tournaments;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/prefectures.yaml のローダ（設計書05 §1.5 / CHANGELOG 28）。47都道府県の JIS順Id → 実名・所属地区。
/// IO はこの層に隔離（不変条件#3）。
/// </summary>
public static class PrefectureTableLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static PrefectureTable LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static PrefectureTable Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<FileDto>(yaml)
            ?? throw new InvalidDataException("prefectures.yaml が空です。");
        var list = (dto.Prefectures ?? new List<PrefDto>())
            .Select(p => new PrefectureInfo(p.Id, p.Name, p.Region))
            .ToList();
        return new PrefectureTable(list);
    }

    private sealed class FileDto
    {
        public List<PrefDto>? Prefectures { get; set; }
    }

    private sealed class PrefDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
    }
}

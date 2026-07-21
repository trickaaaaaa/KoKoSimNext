using KokoSim.Engine.Players;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/injuries.yaml（傷病カタログ）のローダ（設計書03 §3.5）。IO はこの層に隔離（不変条件#3）。
/// 発生率そのものは coefficients.yaml の injury: 側。ここは種類の定義（部位・段階分布・回復倍率・場面重み）だけを読む。
/// 傷病の追加は YAML 編集＋InjuryType enum への1行追加で完結する（不変条件#4）。
/// </summary>
public static class InjuryCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static InjuryCatalog LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static InjuryCatalog Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<FileDto>(yaml)
            ?? throw new InvalidDataException("injuries.yaml が空です。");
        var list = new List<InjuryTypeEntry>();
        foreach (var e in dto.Injuries ?? new List<EntryDto>())
        {
            if (!Enum.TryParse<InjuryType>(e.Id, out var id) || id == InjuryType.None)
                throw new InvalidDataException($"未知の傷病id: {e.Id}");

            var sites = new List<InjurySiteWeight>();
            foreach (var s in e.Sites ?? new List<SiteDto>())
            {
                if (!Enum.TryParse<InjurySite>(s.Site, out var site))
                    throw new InvalidDataException($"未知の部位: {s.Site}（{e.Id}）");
                sites.Add(new InjurySiteWeight(site, s.Weight));
            }

            var scenes = new Dictionary<InjuryScene, double>();
            foreach (var kv in e.Scenes ?? new Dictionary<string, double>())
            {
                scenes[ParseScene(kv.Key, e.Id)] = kv.Value;
            }

            list.Add(new InjuryTypeEntry
            {
                Id = id,
                Name = e.Name,
                Sites = sites,
                MinorShare = e.Severity?.Minor ?? 0.70,
                ModerateShare = e.Severity?.Moderate ?? 0.25,
                RecoveryWeekFactor = e.RecoveryWeekFactor,
                SceneWeights = scenes,
            });
        }
        if (list.Count == 0) throw new InvalidDataException("injuries.yaml に傷病が1件もありません。");
        return new InjuryCatalog(list);
    }

    private static InjuryScene ParseScene(string key, string owner) => key switch
    {
        "weekly" => InjuryScene.Weekly,
        "hit_by_pitch" => InjuryScene.HitByPitch,
        "home_collision" => InjuryScene.HomeCollision,
        "fence_crash" => InjuryScene.FenceCrash,
        "sliding" => InjuryScene.Sliding,
        "overuse" => InjuryScene.Overuse,
        _ => throw new InvalidDataException($"未知の場面: {key}（{owner}）"),
    };

    private sealed class FileDto
    {
        public List<EntryDto>? Injuries { get; set; }
    }

    private sealed class EntryDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public double RecoveryWeekFactor { get; set; } = 1.0;
        public SeverityDto? Severity { get; set; }
        public List<SiteDto>? Sites { get; set; }
        public Dictionary<string, double>? Scenes { get; set; }
    }

    private sealed class SeverityDto
    {
        public double Minor { get; set; } = 0.70;
        public double Moderate { get; set; } = 0.25;
    }

    private sealed class SiteDto
    {
        public string Site { get; set; } = "";
        public double Weight { get; set; } = 1.0;
    }
}

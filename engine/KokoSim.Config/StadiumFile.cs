using KokoSim.Engine.Match.Field;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/stadiums.yaml（球場カタログ）のローダ（設計書13-stadiums §1）。IO はこの層に隔離（不変条件#3）。
/// 寸法（両翼・中堅・フェンス高）だけを読み、①物理層の本塁打判定に効かせる。②風・③ビジュアルは後回し。
/// `stadiums:`（架空基礎タイプ＋聖地）と `prefecture_finals:`（実測ベース47県）の2セクションを結合して返す。
/// 球場の追加は YAML 編集で完結する（不変条件#4）。
/// </summary>
public static class StadiumCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<Stadium> LoadFromFile(string path)
        => Parse(File.ReadAllText(path));

    /// <summary>id → 球場 の辞書でロードする（重複 id は例外）。</summary>
    public static IReadOnlyDictionary<string, Stadium> LoadCatalog(string path)
    {
        var map = new Dictionary<string, Stadium>();
        foreach (var s in LoadFromFile(path))
        {
            if (!map.TryAdd(s.Id, s))
                throw new InvalidDataException($"球場idが重複しています: {s.Id}");
        }
        return map;
    }

    public static IReadOnlyList<Stadium> Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<FileDto>(yaml)
            ?? throw new InvalidDataException("stadiums.yaml が空です。");
        var list = new List<Stadium>();
        AppendAll(list, dto.Stadiums);
        AppendAll(list, dto.PrefectureFinals);
        if (list.Count == 0)
            throw new InvalidDataException("stadiums.yaml に球場が1件もありません。");
        return list;
    }

    private static void AppendAll(List<Stadium> list, List<EntryDto>? entries)
    {
        if (entries is null) return;
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Id))
                throw new InvalidDataException("球場に id がありません。");
            // right 省略時は left にフォールバック（対称球場）。fence_height 省略時は 2.5m。
            var right = e.Right ?? e.Left;
            list.Add(new Stadium
            {
                Id = e.Id,
                Name = e.Name,
                Tier = ParseTier(e.Tier, e.Id),
                LeftM = e.Left,
                RightM = right,
                CenterM = e.Center,
                FenceHeightM = e.FenceHeight,
                Wind = e.Wind,
            });
        }
    }

    private static StadiumTier ParseTier(string? tier, string id) => tier switch
    {
        "municipal" => StadiumTier.Municipal,
        "prefectural" => StadiumTier.Prefectural,
        "national" => StadiumTier.National,
        null or "" => StadiumTier.Prefectural,
        _ => throw new InvalidDataException($"未知の tier: {tier}（{id}）"),
    };

    private sealed class FileDto
    {
        public List<EntryDto>? Stadiums { get; set; }
        public List<EntryDto>? PrefectureFinals { get; set; }
    }

    private sealed class EntryDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public double Left { get; set; } = 95.0;
        public double? Right { get; set; }
        public double Center { get; set; } = 118.0;
        public double FenceHeight { get; set; } = 2.5;
        public string? Tier { get; set; }
        public string? Wind { get; set; }
    }
}

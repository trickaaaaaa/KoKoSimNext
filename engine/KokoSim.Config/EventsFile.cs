using KokoSim.Engine.Season.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>data/events.yaml のローダ（設計書04 §3.1）。</summary>
public static class EventsLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<GameEvent> LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<GameEvent> Parse(string yaml)
    {
        var root = Deserializer.Deserialize<RootDto>(yaml);
        if (root?.Events is null) return Array.Empty<GameEvent>();
        return root.Events.Select(e => e.ToModel()).ToList();
    }

    private sealed class RootDto
    {
        public List<EventDto>? Events { get; set; }
    }

    private sealed class EventDto
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "Random";
        public int? CalendarWeek { get; set; }
        public double Weight { get; set; } = 1.0;
        public int CooldownWeeks { get; set; } = 4;
        public string? SetsAnnualFlag { get; set; }
        public string? RequiresNotAnnualFlag { get; set; }
        public string Text { get; set; } = "";

        public GameEvent ToModel() => new()
        {
            Id = Id,
            Kind = Enum.TryParse<EventKind>(Kind, out var k) ? k : EventKind.Random,
            CalendarWeek = CalendarWeek,
            Weight = Weight,
            CooldownWeeks = CooldownWeeks,
            SetsAnnualFlag = SetsAnnualFlag,
            RequiresNotAnnualFlag = RequiresNotAnnualFlag,
            Text = Text,
        };
    }
}

using KokoSim.Engine.Season.Events;

namespace KokoSim.Config;

/// <summary>
/// data/events.yaml のローダ（設計書04 §3.1）。
///
/// <para>解釈は<b>エンジンの純パーサ</b> <see cref="EventTableParser"/> へ委譲する（issue #609）。
/// Unity は YamlDotNet を持てず StreamingAssets の YAML を自前で読むため、ここで YamlDotNet を使うと
/// 「Balance/テストで読める文言が実ゲームでは読めない」形のずれが起きうる。校名語彙
/// （<c>SchoolNameVocabParser</c>）と同じ作法で1実装に寄せる。</para>
/// </summary>
public static class EventsLoader
{
    public static IReadOnlyList<GameEvent> LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<GameEvent> Parse(string yaml) => EventTableParser.Parse(yaml);
}

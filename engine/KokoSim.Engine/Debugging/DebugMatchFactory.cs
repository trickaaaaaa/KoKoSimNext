using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;

namespace KokoSim.Engine.Debugging;

/// <summary>デバッグ試合の指定（issue #905）。球場以外は全部省略できる。</summary>
public sealed record DebugMatchOptions
{
    /// <summary>試合の母種。省略時は <see cref="DebugMatchFactory.DefaultSeed"/>（決定論＝毎回同じ試合）。</summary>
    public ulong? Seed { get; init; }

    /// <summary>場面ジャンプ（<c>data/debug/scenarios.yaml</c> の1件）。null なら1回表の頭から。</summary>
    public ScenarioDefinition? Scenario { get; init; }
}

/// <summary>
/// セーブ無しで「好きな球場の試合」を1つ組む（issue #905・設計書17 §3.4 の場面ジャンプと併用可）。
/// 両校とも平均的な生成校で、乱数はすべて母種から引く＝同じ指定なら同じ試合になる（不変条件#2）。
/// 本番の試合フロー（大会・練習試合）は通らない＝成績・大会・決定論指紋に一切影響しない。
/// </summary>
public static class DebugMatchFactory
{
    /// <summary>母種の既定値（指定が無いとき）。値自体に意味は無い。</summary>
    public const ulong DefaultSeed = 0x905_0001UL;

    /// <summary>両校の強さ（生成校の平均能力の中心）。</summary>
    public const double TeamStrength = 60.0;

    /// <summary>組み上がった試合一式。<c>MatchProgression</c> へそのまま渡せる。</summary>
    public sealed record Built(Team Away, Team Home, GameContext Ctx, ScenarioStart? Start, ulong Seed);

    public static Built Build(Stadium stadium, DebugMatchOptions? options = null)
    {
        options ??= new DebugMatchOptions();
        var seed = options.Seed ?? DefaultSeed;

        // 観戦する試合と同じ観測ゲート（1球進行・タイムライン）。どれも乱数中立。
        // 天気は渡さない（実ゲームの天気は大会日程から決まる＝ここでは寸法だけを確かめる）。
        var ctx = new GameContext
        {
            CaptureTimelines = true,
            CapturePitches = true,
            PlayerControlsPitcherChanges = true,
            Field = stadium.ToFieldGeometry(),
            StadiumName = stadium.Name,
            StadiumId = stadium.Id,
        };

        if (options.Scenario is { } scenario)
        {
            // 場面ジャンプは受け取った文脈を with で引き継ぐ＝球場は保たれ、ScenarioId が立つ（統計から除外）。
            var built = ScenarioBuilder.Build(scenario, ctx, seed);
            return new Built(built.Away, built.Home, built.Ctx, built.Start, built.Seed);
        }

        var rng = new Xoshiro256Random(seed);
        var away = StrengthTeamFactory.Create(TeamStrength, "遠征校", rng.Fork(1));
        var home = StrengthTeamFactory.Create(TeamStrength, "地元校", rng.Fork(2));
        return new Built(away, home, ctx, null, seed);
    }
}

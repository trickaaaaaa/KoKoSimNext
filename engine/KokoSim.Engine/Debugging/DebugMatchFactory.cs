using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

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
            return new Built(WithAllPitchTypes(built.Away), WithAllPitchTypes(built.Home), built.Ctx, built.Start, built.Seed);
        }

        var rng = new Xoshiro256Random(seed);
        var away = StrengthTeamFactory.Create(TeamStrength, "遠征校", rng.Fork(1));
        var home = StrengthTeamFactory.Create(TeamStrength, "地元校", rng.Fork(2));
        return new Built(WithAllPitchTypes(away), WithAllPitchTypes(home), ctx, null, seed);
    }

    /// <summary>
    /// 全員に全9球種を持たせる（デバッグ試合で球種ごとの絵を確かめるため）。持っていた球種のランクはそのまま、
    /// 足りない球種は球種の素質（<see cref="PitcherAttributes.PitchRank"/>）で埋める。乱数は使わない。
    /// 主将・先発投手は打順・控えと同じ実体なので、変換結果も同じ実体を使い回す（レパートリーの List は参照比較
    /// なので、別々に変換すると record の等価が崩れ「主将が打順に居ない」扱いになる）。
    /// </summary>
    public static Team WithAllPitchTypes(Team team)
    {
        var done = new Dictionary<Player, Player>(ByReference.Instance);
        Player Full(Player p)
        {
            if (done.TryGetValue(p, out var hit)) return hit;
            var have = p.Pitching.EffectiveRepertoire;
            var slots = new List<PitchSlot>();
            foreach (PitchType type in Enum.GetValues(typeof(PitchType)))
            {
                var own = have.FirstOrDefault(s => s.Type == type);
                slots.Add(own ?? new PitchSlot { Type = type, Power = p.Pitching.PitchRank, Sharpness = p.Pitching.PitchRank });
            }
            var full = p with { Pitching = p.Pitching with { Repertoire = slots } };
            done[p] = full;
            return full;
        }

        return team with
        {
            BattingOrder = team.BattingOrder.Select(Full).ToList(),
            Reserves = team.Reserves.Select(Full).ToList(),
            StartingPitcher = team.StartingPitcher is { } sp ? Full(sp) : null,
            Captain = team.Captain is { } c ? Full(c) : null,
        };
    }

    /// <summary>実体の同一性で比べる（netstandard2.1 には ReferenceEqualityComparer が無い）。</summary>
    private sealed class ByReference : IEqualityComparer<Player>
    {
        public static readonly ByReference Instance = new();
        public bool Equals(Player? x, Player? y) => ReferenceEquals(x, y);
        public int GetHashCode(Player obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

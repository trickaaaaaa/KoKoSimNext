using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 1件のシナリオ宣言（設計書17 §3.4, F2）。<c>data/debug/scenarios.yaml</c> の1エントリに対応する。
/// C# にハードコードしない（不変条件#4）。
/// </summary>
public sealed record ScenarioDefinition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>先攻校の指定（"player" / "AI:tier=A" / "AI:strength=72"）。</summary>
    public string Away { get; init; } = "AI:tier=C";
    public string Home { get; init; } = "player";
    public int AwayScore { get; init; }
    public int HomeScore { get; init; }

    public int Inning { get; init; } = 1;
    public bool Top { get; init; } = true;
    public int Outs { get; init; }
    /// <summary>占有塁（1/2/3）。</summary>
    public IReadOnlyList<int> Bases { get; init; } = System.Array.Empty<int>();
    public int Balls { get; init; }
    public int Strikes { get; init; }
    public int Batter { get; init; } = 1;
    public int PitcherFatigue { get; init; }

    /// <summary>現代ルールのトグル（null=ベース GameContext のまま）。</summary>
    public bool? Dh { get; init; }
    public bool? TieBreak { get; init; }

    /// <summary>この場面を回すシード（null=呼び出し側指定）。</summary>
    public ulong? Seed { get; init; }

    /// <summary>強制発動（設計書17 §6.1, F4）。<c>ForcedOutcome</c> の enum 名。null=なし。</summary>
    public string? Force { get; init; }

    public ScenarioStart ToStart() => new()
    {
        Inning = Inning,
        IsTop = Top,
        Outs = Outs,
        OnFirst = Contains(1),
        OnSecond = Contains(2),
        OnThird = Contains(3),
        Balls = Balls,
        Strikes = Strikes,
        AwayScore = AwayScore,
        HomeScore = HomeScore,
        BatterOrder = Batter,
        PitcherFatiguePitches = PitcherFatigue,
    };

    private bool Contains(int b)
    {
        foreach (var x in Bases) if (x == b) return true;
        return false;
    }
}

/// <summary>
/// シナリオ一覧（設計書17 §3.4）。<b>欠損は正常系</b>（Q18-2 確定・2026-07-21）:
/// リリースビルドからは <c>data/debug/</c> をディレクトリ単位で除外するため、
/// ファイルが無い場合は0件で正常に起動し、例外を投げない。
/// </summary>
public sealed class ScenarioCatalog
{
    private readonly Dictionary<string, ScenarioDefinition> _byId;

    public ScenarioCatalog(IEnumerable<ScenarioDefinition> scenarios)
    {
        _byId = new Dictionary<string, ScenarioDefinition>();
        foreach (var s in scenarios)
        {
            if (_byId.ContainsKey(s.Id))
                throw new System.ArgumentException($"シナリオidが重複しています: {s.Id}");
            _byId[s.Id] = s;
        }
    }

    /// <summary>0件のカタログ（<c>data/debug/</c> が無いリリースビルドの正常状態）。</summary>
    public static readonly ScenarioCatalog Empty = new(System.Array.Empty<ScenarioDefinition>());

    public int Count => _byId.Count;
    public IEnumerable<ScenarioDefinition> All => _byId.Values;
    public bool TryGet(string id, out ScenarioDefinition def) => _byId.TryGetValue(id, out def!);
}

/// <summary>
/// シナリオから試合を組む（設計書17 §3.4, F2）。engine 側なので IO は持たない
/// （YAML の読み込みは KokoSim.Config、ファイル探索は CLI/Unity）。
/// </summary>
public static class ScenarioBuilder
{
    /// <summary>組み上がった試合一式。<see cref="GameEngine.NewProgress"/> へそのまま渡せる。</summary>
    public sealed record Built(Team Away, Team Home, GameContext Ctx, ScenarioStart Start, ulong Seed);

    /// <summary>
    /// シナリオを実際の対戦へ落とす。
    /// </summary>
    /// <param name="playerTeam">
    /// "player" 指定を解決するチーム。null なら平均的な生成校で代用する（engine テスト・CLI 用）。
    /// </param>
    public static Built Build(
        ScenarioDefinition def, GameContext baseCtx, ulong seed, Team? playerTeam = null)
    {
        def.ToStart().Validate();

        var away = ResolveTeam(def.Away, "遠征校", seed ^ 0xA_0000UL, playerTeam);
        var home = ResolveTeam(def.Home, "地元校", seed ^ 0xB_0000UL, playerTeam);

        var ctx = baseCtx with { ScenarioId = def.Id };
        if (def.TieBreak is { } tb) ctx = ctx with { TieBreakEnabled = tb };
        if (def.Dh == true)
        {
            away = WithDh(away);
            home = WithDh(home);
        }

        return new Built(away, home, ctx, def.ToStart(), def.Seed ?? seed);
    }

    /// <summary>
    /// "player" / "AI:tier=A" / "AI:strength=72" を Team へ解決する。
    /// AI校は <see cref="StrengthTeamFactory"/>（大会展望と実戦の単一ソース）をそのまま使う。
    /// </summary>
    private static Team ResolveTeam(string spec, string fallbackName, ulong seed, Team? playerTeam)
    {
        if (string.Equals(spec, "player", System.StringComparison.OrdinalIgnoreCase))
        {
            return playerTeam ?? StrengthTeamFactory.Create(55.0, "自校", new Xoshiro256Random(seed));
        }

        var strength = 55.0;
        var name = fallbackName;
        if (spec.StartsWith("AI:", System.StringComparison.OrdinalIgnoreCase))
        {
            var arg = spec.Substring(3);
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                var key = arg.Substring(0, eq).Trim();
                var val = arg.Substring(eq + 1).Trim();
                if (string.Equals(key, "tier", System.StringComparison.OrdinalIgnoreCase))
                {
                    strength = StrengthOfTier(val);
                    name = fallbackName + "(" + val.ToUpperInvariant() + ")";
                }
                else if (string.Equals(key, "strength", System.StringComparison.OrdinalIgnoreCase)
                         && double.TryParse(val, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var s))
                {
                    strength = MathUtil.Clamp(s, 0.0, 100.0);
                }
                else
                {
                    throw new System.ArgumentException($"未知のチーム指定: {spec}（player | AI:tier=A | AI:strength=72）");
                }
            }
        }
        else
        {
            throw new System.ArgumentException($"未知のチーム指定: {spec}（player | AI:tier=A | AI:strength=72）");
        }

        return StrengthTeamFactory.Create(strength, name, new Xoshiro256Random(seed));
    }

    /// <summary>ティア文字 → 帯の中央値（<see cref="Tiers.FromStrength"/> の逆写像）。</summary>
    private static double StrengthOfTier(string tier) => tier.ToUpperInvariant() switch
    {
        "S" => 95.0,
        "A" => 85.0,
        "B" => 75.0,
        "C" => 65.0,
        "D" => 55.0,
        "E" => 45.0,
        "F" => 35.0,
        "G" => 25.0,
        _ => throw new System.ArgumentException($"未知のティア: {tier}（S〜G）"),
    };

    /// <summary>DH制へ組み替える（投手を打順から外し、9番枠をDHにする）。</summary>
    private static Team WithDh(Team t)
    {
        if (t.UsesDh) return t;
        var pitcher = t.BattingOrder[t.PitcherSlot];
        var order = new List<Player>(t.BattingOrder);
        // 投手スロットは控え野手（無ければ打順先頭の複製）で埋め、そこをDH枠にする。
        var dh = t.Bench.Count > 0 ? t.Bench[0] : order[0] with { Name = order[0].Name + "(DH)" };
        order[t.PitcherSlot] = dh;
        var bench = new List<Player>(t.Bench);
        if (bench.Count > 0) bench.RemoveAt(0);
        return t with
        {
            BattingOrder = order,
            Bench = bench,
            DhSlot = t.PitcherSlot,
            StartingPitcher = pitcher,
        };
    }
}

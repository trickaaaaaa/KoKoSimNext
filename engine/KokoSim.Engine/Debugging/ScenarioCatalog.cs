using System.Linq;
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
    /// <summary>守備側投手が動揺した状態で始める（issue #1178）。</summary>
    public bool PitcherRattled { get; init; }

    /// <summary>現代ルールのトグル（null=ベース GameContext のまま）。</summary>
    public bool? Dh { get; init; }
    public bool? TieBreak { get; init; }

    /// <summary>この場面を回すシード（null=呼び出し側指定）。</summary>
    public ulong? Seed { get; init; }

    /// <summary>強制発動（設計書17 §6.1, F4）。<c>ForcedOutcome</c> の enum 名。null=なし。</summary>
    public string? Force { get; init; }

    /// <summary>強制発動を解釈する（null=なし）。未知の綴りは例外（黙って強制なしで始めない）。</summary>
    public ForcedOutcome ResolveForce()
    {
        if (Force is null) return ForcedOutcome.None;
        if (!ForcedOutcomes.TryParse(Force, out var f))
            throw new System.ArgumentException(
                $"シナリオ {Id}: 未知の force: {Force}（{string.Join("|", System.Enum.GetNames(typeof(ForcedOutcome)))}）");
        return f;
    }

    /// <summary>
    /// 宣言が矛盾していないか（範囲外・未知の綴りは黙って直さず投げる＝シナリオYAMLの誤りを読み込み時に殺す）。
    /// YAML の読み込みと <see cref="ScenarioBuilder.Build"/> の両方で呼ぶ。
    /// </summary>
    public void Validate()
    {
        try
        {
            ToStart().Validate();
            var seen = new HashSet<int>();
            foreach (var b in Bases)
            {
                if (b is < 1 or > 3) throw new System.ArgumentException($"シナリオの指定が不正です: bases は1〜3（{b}）");
                if (!seen.Add(b)) throw new System.ArgumentException($"シナリオの指定が不正です: bases に重複（{b}）");
            }
            ScenarioBuilder.ParseTeamSpec(Away);
            ScenarioBuilder.ParseTeamSpec(Home);
            ResolveForce();
        }
        catch (System.ArgumentException e) when (!e.Message.StartsWith("シナリオ " + Id, System.StringComparison.Ordinal))
        {
            throw new System.ArgumentException($"シナリオ {Id}: {e.Message}", e);
        }
    }

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
        PitcherRattled = PitcherRattled,
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
        def.Validate();

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

    /// <summary>チーム指定を解釈した結果。</summary>
    internal readonly record struct TeamSpec(bool IsPlayer, double Strength, string? Tier, int? Bench);

    /// <summary>
    /// チーム指定（<c>player</c> / <c>AI:tier=A</c> / <c>AI:strength=72</c>。末尾に <c>;bench=N</c> を付けられる）を構文だけ解く。
    /// 乱数も生成も使わないので、YAML の読み込み時に誤りを殺すのにも使う。書式違反は <see cref="System.ArgumentException"/>。
    /// </summary>
    internal static TeamSpec ParseTeamSpec(string spec)
    {
        const string usage = "player | AI:tier=A | AI:strength=72";
        // 末尾の ";bench=N"＝ベンチ入りを N 人に絞る（控えを背番号順の後ろから削る）。部員が少ない学校の絵の検証用
        //（サヨナラの場面で喜ぶ人数＝ベンチ入り・issue #1176）。生成の乱数は引き終わった後に削る＝打順・能力は変わらない
        int? bench = null;
        var baseSpec = spec.Trim();
        var semi = spec.IndexOf(';');
        if (semi >= 0)
        {
            var opt = spec.Substring(semi + 1).Trim();
            baseSpec = spec.Substring(0, semi).Trim();
            const string benchKey = "bench=";
            if (!opt.StartsWith(benchKey, System.StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(opt.Substring(benchKey.Length), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
                throw new System.ArgumentException($"未知のチーム指定: {spec}（末尾は ;bench=N だけ）");
            bench = n;
        }

        if (string.Equals(baseSpec, "player", System.StringComparison.OrdinalIgnoreCase))
            return new TeamSpec(true, 55.0, null, bench);

        if (!baseSpec.StartsWith("AI:", System.StringComparison.OrdinalIgnoreCase))
            throw new System.ArgumentException($"未知のチーム指定: {spec}（{usage}）");

        var arg = baseSpec.Substring(3);
        var eq = arg.IndexOf('=');
        if (eq <= 0) throw new System.ArgumentException($"未知のチーム指定: {spec}（{usage}）");
        var key = arg.Substring(0, eq).Trim();
        var val = arg.Substring(eq + 1).Trim();
        if (string.Equals(key, "tier", System.StringComparison.OrdinalIgnoreCase))
            return new TeamSpec(false, StrengthOfTier(val), val.ToUpperInvariant(), bench);
        if (string.Equals(key, "strength", System.StringComparison.OrdinalIgnoreCase)
            && double.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s))
            return new TeamSpec(false, MathUtil.Clamp(s, 0.0, 100.0), null, bench);
        throw new System.ArgumentException($"未知のチーム指定: {spec}（{usage}）");
    }

    /// <summary>
    /// "player" / "AI:tier=A" / "AI:strength=72" を Team へ解決する。
    /// AI校は <see cref="StrengthTeamFactory"/>（大会展望と実戦の単一ソース）をそのまま使う。
    /// </summary>
    private static Team ResolveTeam(string spec, string fallbackName, ulong seed, Team? playerTeam)
    {
        var parsed = ParseTeamSpec(spec);
        Team team;
        if (parsed.IsPlayer)
        {
            team = playerTeam ?? StrengthTeamFactory.Create(parsed.Strength, "自校", new Xoshiro256Random(seed));
        }
        else
        {
            var name = parsed.Tier is null ? fallbackName : fallbackName + "(" + parsed.Tier + ")";
            team = StrengthTeamFactory.Create(parsed.Strength, name, new Xoshiro256Random(seed));
        }

        if (parsed.Bench is not { } bench) return team;
        var keep = System.Math.Max(0, bench - (team.BenchEntry.Count - team.Reserves.Count));
        return keep >= team.Reserves.Count ? team : team with { Reserves = team.Reserves.Take(keep).ToList() };
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
        // 投手スロットは控え（投手枠に置かれていない者を優先。無ければ打順先頭の複製）で埋め、そこをDH枠にする。
        var reserves = new List<Player>(t.Reserves);
        var dhIndex = reserves.FindIndex(p => p.Position != Match.Field.FieldPosition.Pitcher);
        if (dhIndex < 0 && reserves.Count > 0) dhIndex = 0;
        var dh = dhIndex >= 0 ? reserves[dhIndex] : order[0] with { Name = order[0].Name + "(DH)" };
        order[t.PitcherSlot] = dh;
        if (dhIndex >= 0) reserves.RemoveAt(dhIndex);
        return t with
        {
            BattingOrder = order,
            Reserves = reserves,
            DhSlot = t.PitcherSlot,
            StartingPitcher = pitcher,
        };
    }
}

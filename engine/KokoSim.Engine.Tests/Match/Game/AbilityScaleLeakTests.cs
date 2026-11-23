using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 表示能力の物差し（issue #1080）の<b>漏れ検出</b>: 能力がばらけた固定チームを「今の物差しの値（物理Lv）・Δ=0」で
/// 戦わせた試合と、同じチームを一律 −Δ（表示Lv）にして既定の Δ=7 で戦わせた試合が、1ビット一致すること。
/// 試合の中で能力の絶対値を読む所（物理の入口・采配AIの閾値・主将の統率力・継投の人選・伝令など）が
/// <see cref="AbilityEffectCoefficients.Delta"/> を通らず生の Lv を読んでいれば、ここが赤くなる。
///
/// <para>能力は 15〜95（今の物差し）に散らす＝表示では 8〜88。天井（99）・下限（1）に当たらない範囲だけを見る
/// （天井・下限の扱いはわざと変えている＝issue #1080 の狙い）。全員50の決定論カードでは閾値の手前・向こうを
/// またがないので、閾値の読み漏れはこのテストでしか見えない。</para>
/// </summary>
public sealed class AbilityScaleLeakTests
{
    private const int Seeds = 24;

    public static IEnumerable<object[]> Configs()
    {
        yield return new object[] { "plain" };
        yield return new object[] { "standard" };
        yield return new object[] { "ai-smallball" };
        yield return new object[] { "ai-power" };
        yield return new object[] { "ai-defensive" };
        yield return new object[] { "ai-total" };
        yield return new object[] { "dh-weather" };
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void ShiftedTeams_WithDelta_MatchUnshiftedTeams_WithoutDelta(string config)
    {
        // 試合ごとに状態を作り直す（シード事前確定）＝並列でも決定論（.claude/rules/engine-tests.md）。
        var failures = new string?[Seeds];
        System.Threading.Tasks.Parallel.For(0, Seeds, i =>
        {
            var seed = (ulong)(i + 1);
            // 今の物差しの世界（物理Lvの選手・Δ=0）と、表示Lvの世界（一律 −Δ の選手・既定の Δ）。
            var (away0, home0, ctx0) = Build(config, seed, 0.0);
            var (away1, home1, ctx1) = Build(config, seed, AbilityScale.Delta);
            var expected = GameResultDigest.Canonical(GameEngine.Play(away0, home0, ctx0, new Xoshiro256Random(seed)));
            var actual = GameResultDigest.Canonical(GameEngine.Play(
                AbilityScale.ToDisplay(away1), AbilityScale.ToDisplay(home1), ctx1, new Xoshiro256Random(seed)));
            if (expected != actual) failures[i] = $"[{config} seed {seed}] 最初の差: {FirstDiff(expected, actual)}";
        });
        foreach (var f in failures)
            if (f is not null) Assert.Fail(f);
    }

    private static string FirstDiff(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (var i = 0; i < System.Math.Min(la.Length, lb.Length); i++)
            if (la[i] != lb[i]) return $"行{i}: 期待「{la[i].Trim()}」実際「{lb[i].Trim()}」";
        return $"行数 {la.Length} vs {lb.Length}";
    }

    private static (Team Away, Team Home, GameContext Ctx) Build(string config, ulong seed, double delta)
    {
        var rng = new Xoshiro256Random(0xA11C_E000UL ^ seed);
        var useDh = config == "dh-weather";
        var away = VariedTeam("A", rng, useDh);
        var home = VariedTeam("H", rng, useDh);
        // 采配は自前の係数束で能力を読む＝試合と同じ Δ の束を渡す（本番では両方とも既定＝同じ Δ）。
        ITacticsBrain? Brain(double d)
        {
            var br = new BaserunningCoefficients { AbilityEffect = new AbilityEffectCoefficients { Delta = d } };
            return config switch
            {
                "plain" => null,
                "standard" or "dh-weather" => new StandardTacticsBrain(baserunning: br),
                "ai-smallball" => new AiTacticsBrain(new AiProfile(TacticalSense: 85, TierRank: 5, SchoolStyle.SmallBall), baserunning: br),
                "ai-power" => new AiTacticsBrain(new AiProfile(TacticalSense: 70, TierRank: 6, SchoolStyle.PowerHitting), baserunning: br),
                "ai-defensive" => new AiTacticsBrain(new AiProfile(TacticalSense: 90, TierRank: 4, SchoolStyle.DefensiveMinded), baserunning: br),
                _ => new AiTacticsBrain(new AiProfile(TacticalSense: 80, TierRank: 5, SchoolStyle.TotalBaseball), baserunning: br),
            };
        }
        away = away with { Tactics = Brain(delta) };
        home = home with { Tactics = Brain(delta) };
        var ctx = useDh
            ? DeterminismCards.DhWeatherContext
            : new GameContext { TieBreakEnabled = true, TieBreakStartInning = 10, MercyRuleEnabled = true };
        return (away, home, ctx.WithAbilityEffect(ctx.Batting.AbilityEffect with { Delta = delta }));
    }

    private static int Lv(IRandomSource r) => r.NextInt(15, 96);

    private static Player Varied(string name, FieldPosition pos, IRandomSource r, int grade)
    {
        var p = new Player
        {
            Name = name, Position = pos, Grade = grade,
            Contact = Lv(r), Power = Lv(r), LaunchTendency = r.NextInt(20, 81), Discipline = Lv(r),
            Speed = Lv(r), ArmStrength = Lv(r), ThrowAccuracy = Lv(r), Fielding = Lv(r), Catching = Lv(r),
            Lead = Lv(r), Bunt = Lv(r), Steal = Lv(r), Baserunning = Lv(r), Mental = Lv(r),
            Leadership = r.NextInt(20, 81),
            Bats = r.NextDouble() < 0.3 ? Handedness.Left : Handedness.Right,
            Throws = r.NextDouble() < 0.2 ? Handedness.Left : Handedness.Right,
            Pitching = new PitcherAttributes
            {
                MaxVelocityKmh = 120.0 + 30.0 * r.NextDouble(),
                Control = Lv(r), PitchRank = Lv(r),
                StaminaPitches = 75.0 + 50.0 * r.NextDouble(),
                Repertoire = new[]
                {
                    new PitchSlot { Type = PitchType.Fastball, Power = Lv(r), Sharpness = Lv(r) },
                    new PitchSlot { Type = PitchType.Slider, Power = Lv(r), Sharpness = Lv(r) },
                    new PitchSlot { Type = PitchType.Fork, Power = Lv(r), Sharpness = Lv(r) },
                },
            },
        };
        var types = new[] { Personality.Introvert, Personality.MoodMaker, Personality.HotBlood, Personality.LoneWolf, Personality.Normal };
        return p.WithAxes(types[r.NextInt(0, types.Length)]);
    }

    private static Team VariedTeam(string name, IRandomSource r, bool useDh)
    {
        var positions = new[]
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };
        var order = new List<Player>();
        for (var i = 0; i < positions.Length; i++)
            order.Add(Varied($"{name}{i}", positions[i], r, 1 + i % 3));
        var pitcher = Varied(name + "P", FieldPosition.Pitcher, r, 3);
        order.Add(pitcher);
        var bullpen = new List<Player>();
        for (var i = 0; i < 3; i++) bullpen.Add(Varied($"{name}R{i}", FieldPosition.Pitcher, r, 1 + i % 3));
        var bench = new List<Player>();
        for (var i = 0; i < 5; i++) bench.Add(Varied($"{name}B{i}", positions[(i * 3) % positions.Length], r, 1 + i % 3));
        var team = new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(bullpen, bench),
            Captain = order[2],
        };
        if (!useDh) return team;
        var dhOrder = new List<Player>(order);
        dhOrder[8] = Varied(name + "DH", FieldPosition.FirstBase, r, 2);
        return team with { BattingOrder = dhOrder, DhSlot = 8, StartingPitcher = pitcher };
    }
}

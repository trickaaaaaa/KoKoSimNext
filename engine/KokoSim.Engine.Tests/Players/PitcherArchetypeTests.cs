using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// 球速の Level→km/h 変換と球質タイプ（本格派/技巧派/軟投派）を検証する。
/// 狙いは「球速＝投手の良さ」をやめ、<b>同じ投手総合に速球派と技巧派が併存する</b>こと。
/// </summary>
public sealed class PitcherArchetypeTests
{
    private static School Sch(int id, double strength)
        => new() { Id = id, Name = "テスト校", PrefectureId = 0, Strength = strength };

    private static IEnumerable<Player> PitchersOf(Team t)
        => t.BattingOrder.Concat(t.Bullpen).Where(p => p.Pitching is not null);

    // ===== 変換式（不変条件#1: 表示層→物理層は一箇所集約） =====

    [Fact]
    public void VelocityFromLevel_MatchesLeagueAverage_AtLevel50()
    {
        // PitcherAttributes.LeagueAverage の記述「球速132km/h, 各50」と一致すること。
        Assert.Equal(132.0, PitcherAttributes.VelocityKmhFromLevel(50), 1);
        Assert.Equal(PitcherAttributes.LeagueAverage.MaxVelocityKmh,
            PitcherAttributes.VelocityKmhFromLevel(50), 1);
    }

    [Fact]
    public void VelocityFromLevel_SpansRealisticHighSchoolRange()
    {
        var lo = PitcherAttributes.VelocityKmhFromLevel(20);
        var top = PitcherAttributes.VelocityKmhFromLevel(99);

        Assert.InRange(lo, 112, 122);    // 弱小校の投手
        Assert.InRange(top, 152, 158);   // 全国級の剛腕
        // 上限に張り付かず最上位帯でも差が残る（旧式は Lv80 以上が全部155で潰れていた）。
        Assert.True(PitcherAttributes.VelocityKmhFromLevel(99) > PitcherAttributes.VelocityKmhFromLevel(85));
        Assert.True(PitcherAttributes.VelocityKmhFromLevel(85) > PitcherAttributes.VelocityKmhFromLevel(70));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(80)]
    [InlineData(99)]
    public void VelocityLevel_RoundTrips(int level)
        => Assert.Equal(level, PitcherAttributes.LevelFromVelocityKmh(
            PitcherAttributes.VelocityKmhFromLevel(level)));

    [Fact]
    public void StaminaLevel_RoundTrips()
        => Assert.Equal(64, PitcherAttributes.LevelFromStaminaPitches(
            PitcherAttributes.StaminaPitchesFromLevel(64)));

    // ===== ① 相手校の球速に個体差がある（旧実装は強さの決定関数だった） =====

    [Fact]
    public void AiSchoolAces_HaveVelocitySpread_AtEqualTeamStrength()
    {
        var aces = Enumerable.Range(1, 120)
            .Select(id => StrengthTeamFactory.ForSchool(Sch(id, 70), 1))
            .Select(t => t.BattingOrder[t.PitcherSlot].Pitching!.MaxVelocityKmh)
            .ToList();

        // 同じ強さでも球速はばらつく（＝同じ強さの学校のエースが全員同じ球速にならない）。
        Assert.True(aces.Distinct().Count() > 20, $"球速の種類が少なすぎる: {aces.Distinct().Count()}");
        Assert.True(aces.Max() - aces.Min() > 15, $"球速の幅が狭すぎる: {aces.Max() - aces.Min():F1}km/h");
    }

    [Fact]
    public void AiSchoolAces_AreNotAllFlamethrowers()
    {
        // 県上位（強さ72）でも150km/h超は少数派であること（高校野球としての妥当性）。
        var aces = Enumerable.Range(1, 200)
            .Select(id => StrengthTeamFactory.ForSchool(Sch(id, 72), 1))
            .Select(t => t.BattingOrder[t.PitcherSlot].Pitching!.MaxVelocityKmh)
            .ToList();

        var over150 = aces.Count(v => v >= 150) / (double)aces.Count;
        Assert.InRange(over150, 0.0, 0.30);
        Assert.InRange(aces.Average(), 132, 148);
    }

    // ===== ③ 球質タイプ =====

    [Fact]
    public void Archetypes_AppearWithConfiguredShares()
    {
        var c = new PitcherArchetypeCoefficients();
        var types = Enumerable.Range(1, 300)
            .SelectMany(id => PitchersOf(StrengthTeamFactory.ForSchool(Sch(id, 68), 1)))
            .Select(p => p.Pitching!.Archetype)
            .ToList();

        Assert.All(types, t => Assert.NotNull(t));   // 生成時の型が保持されている
        double Share(PitcherArchetype a) => types.Count(t => t == a) / (double)types.Count;

        Assert.InRange(Share(PitcherArchetype.Power), c.PowerShare - 0.06, c.PowerShare + 0.06);
        Assert.InRange(Share(PitcherArchetype.Finesse), c.FinesseShare - 0.06, c.FinesseShare + 0.06);
        Assert.InRange(Share(PitcherArchetype.SoftToss), c.SoftTossShare - 0.05, c.SoftTossShare + 0.05);
    }

    [Fact]
    public void PowerPitchers_ThrowHarder_ButFinesseHasBetterControl()
    {
        var pitchers = Enumerable.Range(1, 300)
            .SelectMany(id => PitchersOf(StrengthTeamFactory.ForSchool(Sch(id, 68), 1)))
            .ToList();

        double AvgVelocity(PitcherArchetype a) => pitchers
            .Where(p => p.Pitching!.Archetype == a).Average(p => p.Pitching!.MaxVelocityKmh);
        double AvgControl(PitcherArchetype a) => pitchers
            .Where(p => p.Pitching!.Archetype == a).Average(p => p.Pitching!.Control);

        // 球速が型を分ける主軸（本格派 > 技巧派 > 軟投派）。
        Assert.True(AvgVelocity(PitcherArchetype.Power) > AvgVelocity(PitcherArchetype.Finesse) + 5);
        Assert.True(AvgVelocity(PitcherArchetype.Finesse) > AvgVelocity(PitcherArchetype.SoftToss));
        // 制球は逆順だが差は小さい。現行シムでは制球の価値が球速より大きく、大きく振ると
        // 本格派が一方的に不利になるため（実測で校正済み・Archetypes_AreBalanceNeutral_InActualGames 参照）。
        Assert.True(AvgControl(PitcherArchetype.SoftToss) > AvgControl(PitcherArchetype.Power) + 3);
        Assert.True(AvgControl(PitcherArchetype.Finesse) > AvgControl(PitcherArchetype.Power));
    }

    /// <summary>
    /// ★型は<b>実戦で</b>バランス中立: 型を変えても1試合あたりの失点がほぼ揃うこと。
    /// 合成式（球速.40/制球.25…）の加重和で0になることを見る"式の中立"は循環論法なので採らない
    /// ＝オフセットは式に合わせて設計したのだから式では必ず0になる。実際に試合を回して確かめる。
    /// 現行シムは制球（与四球）の価値が球速（奪三振）より大きく、素朴な「球速+12/制球-10」では
    /// 本格派の失点が 3.10 対 技巧派 2.71 と明確に不利だった。その再発を防ぐ回帰。
    /// </summary>
    [Fact]
    [Trait("Category", "Heavy")]   // 型4種×400試合のフルエンジン実測
    public void Archetypes_AreBalanceNeutral_InActualGames()
    {
        const int baseLv = 68;
        const int games = 400;
        var c = new PitcherArchetypeCoefficients();
        var ctx = new GameContext();

        var runsAllowed = new Dictionary<PitcherArchetype, double>();
        foreach (PitcherArchetype a in System.Enum.GetValues(typeof(PitcherArchetype)))
        {
            var (dv, dc, ds, dr) = PitcherArchetypes.Offsets(a, c);
            int L(double x) => (int)MathUtil.Clamp(System.Math.Round(x), 10, 99);
            var mine = FixedTeam(L(baseLv + dv), L(baseLv + dc), L(baseLv + ds), L(baseLv + dr), "自");
            var foe = FixedTeam(baseLv, baseLv, baseLv, baseLv, "敵");

            double runs = 0;
            for (var i = 0; i < games; i++)
                runs += GameEngine.Play(foe, mine, ctx, new Xoshiro256Random((ulong)(i * 7919 + 13))).AwayRuns;
            runsAllowed[a] = runs / games;
        }

        var spread = runsAllowed.Values.Max() - runsAllowed.Values.Min();
        Assert.True(spread < 0.25,
            "型ごとの失点差が大きすぎる（実戦でバランス中立でない）: "
            + string.Join(" / ", runsAllowed.Select(kv => $"{PitcherArchetypes.Label(kv.Key)}{kv.Value:F2}")));
    }

    // 能力を指定した投手＋固定打線のチーム（実測の対照条件をそろえるため）。
    private static Team FixedTeam(int velLv, int ctrlLv, int stamLv, int rankLv, string name)
    {
        Player Bat(FieldPosition pos) => new()
        {
            Name = pos.ToString(), Position = pos,
            Contact = 55, Power = 55, LaunchTendency = 50, Discipline = 55,
            Speed = 55, ArmStrength = 55, ThrowAccuracy = 55, Fielding = 55, Catching = 55,
            Bunt = 55, Steal = 55, Baserunning = 55,
        };
        var pitcher = new Player
        {
            Name = name + "P", Position = FieldPosition.Pitcher,
            Contact = 30, Power = 30, LaunchTendency = 50, Discipline = 50,
            Speed = 50, ArmStrength = 50, ThrowAccuracy = 50, Fielding = 50, Catching = 50,
            Bunt = 50, Steal = 50, Baserunning = 50,
            Pitching = new PitcherAttributes
            {
                MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velLv),
                Control = ctrlLv,
                StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(stamLv),
                PitchRank = rankLv,
                Repertoire = new[]
                {
                    PitchSlot.FastballOf(rankLv),
                    new PitchSlot { Type = PitchType.Slider, Power = rankLv, Sharpness = rankLv },
                    new PitchSlot { Type = PitchType.Fork, Power = rankLv, Sharpness = rankLv },
                },
            },
        };
        var order = new List<Player>
        {
            Bat(FieldPosition.Catcher), Bat(FieldPosition.FirstBase),
            Bat(FieldPosition.SecondBase), Bat(FieldPosition.ThirdBase),
            Bat(FieldPosition.Shortstop), Bat(FieldPosition.LeftField),
            Bat(FieldPosition.CenterField), Bat(FieldPosition.RightField),
            pitcher,
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[] { pitcher with { Name = name + "R1" }, pitcher with { Name = name + "R2" } },
        };
    }

    [Fact]
    public void Sample_IsDeterministic()
    {
        var c = new PitcherArchetypeCoefficients();
        var a = PitcherArchetypes.Sample(new Xoshiro256Random(99), c);
        var b = PitcherArchetypes.Sample(new Xoshiro256Random(99), c);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Labels_AreJapanese()
    {
        Assert.Equal("本格派", PitcherArchetypes.Label(PitcherArchetype.Power));
        Assert.Equal("技巧派", PitcherArchetypes.Label(PitcherArchetype.Finesse));
        Assert.Equal("軟投派", PitcherArchetypes.Label(PitcherArchetype.SoftToss));
        Assert.Equal("バランス型", PitcherArchetypes.Label(PitcherArchetype.Balanced));
    }
}

using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 打順編成＋DH使用判断の純関数（issue #54, 設計書11 §4）。乱数を使わない決定論変換なので、
/// 手作りの Player/Team フィクスチャで直接検証する（StrengthTeamFactory の乱数分布に依存しない）。
/// </summary>
public sealed class LineupOrdererTests
{
    private static readonly LineupCoefficients C = new();

    private static Player P(string name, int contact = 50, int power = 50, int discipline = 50,
        int speed = 50, int bunt = 50, FieldPosition pos = FieldPosition.CenterField)
        => new()
        {
            Name = name, Position = pos, Contact = contact, Power = power,
            Discipline = discipline, Speed = speed, Bunt = bunt,
        };

    private static Player Pitcher(string name, int contact = 30, int power = 30)
        => new()
        {
            Name = name, Position = FieldPosition.Pitcher, Contact = contact, Power = power,
            Pitching = new PitcherAttributes(),
        };

    // ===== Arrange: 能力ベースの打順編成 =====

    [Fact]
    public void Arrange_LeadoffIsBestOnBaseHitter()
    {
        var batters = new List<Player>
        {
            P("A", discipline: 60, contact: 60, speed: 60),
            P("出塁王", discipline: 90, contact: 80, speed: 85),
            P("C", discipline: 50, contact: 50, speed: 50),
        };
        var result = LineupOrderer.Arrange(batters, C);
        Assert.Equal("出塁王", result[0].Name);
    }

    [Fact]
    public void Arrange_SecondIsBestSmallBallHitter_AmongRemaining()
    {
        var batters = new List<Player>
        {
            P("出塁王", discipline: 90, contact: 80, speed: 85),
            P("小技巧者", contact: 85, bunt: 90),
            P("C", contact: 40, bunt: 30),
        };
        var result = LineupOrderer.Arrange(batters, C);
        Assert.Equal("出塁王", result[0].Name);
        Assert.Equal("小技巧者", result[1].Name);
    }

    [Fact]
    public void Arrange_CleanupSlot_IsStrongestOverallHitter()
    {
        // 出塁力・小技（discipline/speed/bunt）とミート(contact)は全員共通にし、パワーだけ突出させる
        // ＝1・2番の判定に影響を与えず「打撃総合」だけで中軸が決まることを見る。
        var batters = Enumerable.Range(0, 8)
            .Select(i => P($"並{i}", contact: 45, power: 45, discipline: 20, speed: 20, bunt: 20))
            .ToList();
        batters[5] = P("主砲", contact: 45, power: 95, discipline: 20, speed: 20, bunt: 20);

        var result = LineupOrderer.Arrange(batters, C);

        Assert.Equal("主砲", result[3].Name); // 4番（0-based index 3）
    }

    [Fact]
    public void Arrange_ThirdAndFifth_AreNextBestOverall_InDescendingOrder()
    {
        // 出塁力・小技・ミートは全員共通（1・2番はタイブレークで先頭2人に落ち着く）。
        // パワーだけ差をつけて、中軸(4→3→5番)の補充順を見る。
        var batters = Enumerable.Range(0, 8)
            .Select(i => P($"並{i}", contact: 40, power: 40, discipline: 20, speed: 20, bunt: 20))
            .ToList();
        batters[5] = P("1位", contact: 40, power: 95, discipline: 20, speed: 20, bunt: 20);
        batters[6] = P("2位", contact: 40, power: 85, discipline: 20, speed: 20, bunt: 20);
        batters[7] = P("3位", contact: 40, power: 75, discipline: 20, speed: 20, bunt: 20);

        var result = LineupOrderer.Arrange(batters, C);

        Assert.Equal("1位", result[3].Name);  // 4番=最強
        Assert.Equal("2位", result[2].Name);  // 3番=次点
        Assert.Equal("3位", result[4].Name);  // 5番=その次
    }

    [Fact]
    public void Arrange_ResidualSlots_AreDescendingByOwnOverallScore()
    {
        // 役割（1・2番・中軸）が誰に付くかに関わらず、6番以降に落ちた残り全員は
        // 自分たちの打撃総合の高い順に並ぶという不変条件を見る。
        var rng = new Xoshiro256Random(12345);
        int Roll() => 15 + (int)(rng.NextDouble() * 80);
        var batters = Enumerable.Range(0, 9)
            .Select(i => P($"選手{i}",
                contact: Roll(), power: Roll(), discipline: Roll(), speed: Roll(), bunt: Roll()))
            .ToList();

        var result = LineupOrderer.Arrange(batters, C);

        var residualScores = result.Skip(5).Select(p => LineupOrderer.BattingScore(p, C)).ToList();
        for (var i = 1; i < residualScores.Count; i++)
        {
            Assert.True(residualScores[i - 1] >= residualScores[i],
                $"残り枠は打撃総合の降順であるべき: {string.Join(", ", residualScores)}");
        }
    }

    [Fact]
    public void Arrange_PreservesAllPlayers_NoDuplicatesNoDrops()
    {
        var batters = Enumerable.Range(0, 8).Select(i => P($"選手{i}", contact: 40 + i * 5)).ToList();
        var result = LineupOrderer.Arrange(batters, C);

        Assert.Equal(batters.Count, result.Count);
        Assert.Equal(batters.OrderBy(p => p.Name).Select(p => p.Name),
            result.OrderBy(p => p.Name).Select(p => p.Name));
    }

    // ===== ShouldUseDh: 投手の打撃総合としきい値 =====

    [Fact]
    public void ShouldUseDh_WeakHittingPitcher_ReturnsTrue()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("ノーコン剛腕", contact: 20, power: 20); // 打撃総合20 vs 野手平均55 → gap35
        Assert.True(LineupOrderer.ShouldUseDh(pitcher, fielders, C));
    }

    [Fact]
    public void ShouldUseDh_StrongHittingPitcher_ReturnsFalse()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("二刀流", contact: 60, power: 60); // 野手平均を上回る打撃型エース
        Assert.False(LineupOrderer.ShouldUseDh(pitcher, fielders, C));
    }

    [Fact]
    public void ShouldUseDh_GapBelowThreshold_ReturnsFalse()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("並み打撃", contact: 48, power: 48); // gap=7 < 既定しきい値15
        Assert.False(LineupOrderer.ShouldUseDh(pitcher, fielders, C));
    }

    // ===== Compose: Team全体への適用（DhSlot・StartingPitcher・Benchの整合） =====

    private static Team BuildTeam(Player pitcher, IReadOnlyList<Player> fielders, IReadOnlyList<Player> bench)
        => new() { Name = "テスト校", BattingOrder = fielders.Append(pitcher).ToList(), PitcherSlot = 8, Bench = bench };

    [Fact]
    public void Compose_WithoutModernRules_NeverUsesDh_PitcherStaysAtSlot8()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 50 + i)).ToList();
        var pitcher = Pitcher("投手", contact: 10, power: 10);
        var team = BuildTeam(pitcher, fielders, new List<Player> { P("控え", contact: 90, power: 90) });

        var result = LineupOrderer.Compose(team, C, modernRules: null, calendarYear: null);

        Assert.False(result.UsesDh);
        Assert.Equal(8, result.PitcherSlot);
        Assert.Same(pitcher, result.BattingOrder[8]);
        Assert.Equal(9, result.BattingOrder.Count);
    }

    [Fact]
    public void Compose_ModernRulesBeforeIntroYear_DoesNotUseDh_EvenForWeakPitcher()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("非力投手", contact: 10, power: 10);
        var team = BuildTeam(pitcher, fielders, new List<Player> { P("控え", contact: 90, power: 90) });

        var result = LineupOrderer.Compose(team, C, new ModernRules(), calendarYear: 2020);

        Assert.False(result.UsesDh);
    }

    [Fact]
    public void Compose_ModernRulesAfterIntroYear_WeakHittingPitcher_UsesDh()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("非力投手", contact: 10, power: 10);
        var bestBench = P("最強控え", contact: 95, power: 95);
        var bench = new List<Player> { P("並み控え", contact: 50, power: 50), bestBench };
        var team = BuildTeam(pitcher, fielders, bench);

        var result = LineupOrderer.Compose(team, C, new ModernRules(), calendarYear: 2026);

        Assert.True(result.UsesDh);
        Assert.Same(pitcher, result.StartingPitcher);
        Assert.Equal(-1, result.PitcherSlot);
        Assert.Equal(9, result.BattingOrder.Count);
        Assert.All(result.BattingOrder, p => Assert.Null(p.Pitching));           // 打順に投手がいない
        Assert.Same(bestBench, result.BattingOrder[result.DhSlot]);              // 最強控えが繰り上げ
        Assert.DoesNotContain(bestBench, result.Bench);                         // ベンチから抜ける
        Assert.Single(result.Bench);
    }

    [Fact]
    public void Compose_ModernRulesAfterIntroYear_StrongHittingPitcher_DoesNotUseDh()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("二刀流エース", contact: 65, power: 65);
        var team = BuildTeam(pitcher, fielders, new List<Player> { P("控え", contact: 90, power: 90) });

        var result = LineupOrderer.Compose(team, C, new ModernRules(), calendarYear: 2026);

        Assert.False(result.UsesDh);
        Assert.Same(pitcher, result.BattingOrder[8]);
    }
}

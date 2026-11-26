using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
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

    // ===== BatsWithFielders: 投手が野手と同じ土俵で打順に入るか（issue #360） =====

    [Fact]
    public void BatsWithFielders_PitcherAboveFielderAverage_ReturnsTrue()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("打撃型エース", contact: 60, power: 60);
        Assert.True(LineupOrderer.BatsWithFielders(pitcher, fielders, C));
    }

    [Fact]
    public void BatsWithFielders_PitcherBelowFielderAverage_ReturnsFalse()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("非力投手", contact: 50, power: 50); // 野手平均55を下回る
        Assert.False(LineupOrderer.BatsWithFielders(pitcher, fielders, C));
    }

    [Fact]
    public void BatsWithFielders_GapCoefficient_RaisesTheBar()
    {
        // 係数を上げると「平均より少し上」では足りなくなる（バランス調整はYAMLで, 不変条件#4）。
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("平均超え", contact: 60, power: 60); // 平均+5
        var strict = new LineupCoefficients { PitcherBattingOrderGap = 10.0 };
        Assert.False(LineupOrderer.BatsWithFielders(pitcher, fielders, strict));
    }

    // ===== Compose: Team全体への適用（DhSlot・StartingPitcher・Benchの整合） =====

    private static Team BuildTeam(Player pitcher, IReadOnlyList<Player> fielders, IReadOnlyList<Player> bench)
        => new() { Name = "テスト校", BattingOrder = fielders.Append(pitcher).ToList(), PitcherSlot = 8, Reserves = bench };

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
        Assert.All(result.BattingOrder, p => Assert.NotEqual(FieldPosition.Pitcher, p.Position)); // 打順に投手がいない
        Assert.Same(bestBench, result.BattingOrder[result.DhSlot]);              // 最強控えが繰り上げ
        Assert.DoesNotContain(bestBench, result.Reserves);                         // ベンチから抜ける
        Assert.Single(result.Reserves);
    }

    [Fact]
    public void Compose_ModernRulesAfterIntroYear_StrongHittingPitcher_DoesNotUseDh()
    {
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("二刀流エース", contact: 65, power: 65);
        var team = BuildTeam(pitcher, fielders, new List<Player> { P("控え", contact: 90, power: 90) });

        var result = LineupOrderer.Compose(team, C, new ModernRules(), calendarYear: 2026);

        Assert.False(result.UsesDh);
        // 打撃型エースは9番固定でなく野手と同じ土俵（issue #360）。投手枠は並びに追従する（issue #352）。
        Assert.Same(pitcher, result.BattingOrder[result.PitcherSlot]);
    }

    // ===== 投手の打順は打力で決める（issue #360） =====

    [Fact]
    public void Compose_BattingTypeAce_TakesCleanupSlot_PitcherSlotFollows()
    {
        // 野手全員より打撃総合が高いエース → 最強打者の枠＝4番（index 3）に入る（「エースで4番」）。
        // 出塁系・小技は低くして1番・2番の役割に取られないようにする（4番の枠取りだけを見る）。
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("打撃型エース", contact: 70, power: 70)
            with { Discipline = 20, Speed = 20, Bunt = 10 };
        var team = BuildTeam(pitcher, fielders, new List<Player>());

        var result = LineupOrderer.Compose(team, C, modernRules: null, calendarYear: null);

        Assert.Same(pitcher, result.BattingOrder[3]);
        Assert.Equal(3, result.PitcherSlot);
        Assert.Equal(9, result.BattingOrder.Count);
    }

    [Fact]
    public void Compose_WeakHittingPitcher_StaysAtNinth()
    {
        // 野手平均に届かない投手は従来どおり9番（issue #360 の既定側）。
        var fielders = Enumerable.Range(0, 8).Select(i => P($"野手{i}", contact: 55, power: 55)).ToList();
        var pitcher = Pitcher("非力投手", contact: 30, power: 30);
        var team = BuildTeam(pitcher, fielders, new List<Player>());

        var result = LineupOrderer.Compose(team, C, modernRules: null, calendarYear: null);

        Assert.Same(pitcher, result.BattingOrder[8]);
        Assert.Equal(8, result.PitcherSlot);
    }

    // ===== 校風・左右のバランス（設計書11 §4 オーダー編成） =====

    private static Player Filler(string name) => P(name, contact: 30, power: 30, discipline: 30, speed: 30, bunt: 30);

    private static List<Player> NineWith(params Player[] special)
        => special.Concat(Enumerable.Range(0, 9 - special.Length).Select(i => Filler($"控え{i}"))).ToList();

    [Fact]
    public void Arrange_SmallBallStyle_PutsFastRunnerAtLeadoff()
    {
        var batters = NineWith(
            P("出塁型", discipline: 80, contact: 60, speed: 40, bunt: 20),
            P("俊足", discipline: 55, contact: 55, speed: 80, bunt: 20));

        Assert.Equal("出塁型", LineupOrderer.Arrange(batters, C)[0].Name);
        Assert.Equal("出塁型", LineupOrderer.Arrange(batters, C, SchoolStyle.Standard)[0].Name);
        Assert.Equal("俊足", LineupOrderer.Arrange(batters, C, SchoolStyle.SmallBall)[0].Name);
    }

    [Fact]
    public void Arrange_SmallBallStyle_PrefersFastRunnerAtSecond()
    {
        var batters = NineWith(
            P("1番候補", discipline: 95, contact: 95, speed: 95, bunt: 20),
            P("小技型", discipline: 20, contact: 60, speed: 40, bunt: 70),
            P("俊足", discipline: 20, contact: 50, speed: 90, bunt: 50));

        Assert.Equal("小技型", LineupOrderer.Arrange(batters, C)[1].Name);
        Assert.Equal("俊足", LineupOrderer.Arrange(batters, C, SchoolStyle.SmallBall)[1].Name);
    }

    [Fact]
    public void Arrange_PowerHittingStyle_PutsSluggerAtCleanup()
    {
        // 1・2番は別の2人が取る（出塁・小技に全振り）。中軸は A（ミート型）vs B（長打型）。
        var batters = NineWith(
            P("1番", discipline: 95, contact: 90, power: 10, speed: 95, bunt: 20),
            P("2番", discipline: 20, contact: 90, power: 10, speed: 20, bunt: 95),
            P("ミート型", contact: 80, power: 50, discipline: 20, speed: 20, bunt: 20),
            P("長打型", contact: 50, power: 70, discipline: 20, speed: 20, bunt: 20));

        Assert.Equal("ミート型", LineupOrderer.Arrange(batters, C)[3].Name);
        Assert.Equal("ミート型", LineupOrderer.Arrange(batters, C, SchoolStyle.SmallBall)[3].Name);
        var power = LineupOrderer.Arrange(batters, C, SchoolStyle.PowerHitting);
        Assert.Equal("長打型", power[3].Name);
        Assert.Equal("ミート型", power[2].Name);
    }

    [Fact]
    public void Arrange_StyleNullOrStandard_IsIdenticalToAbilityOnly()
    {
        var rng = new Xoshiro256Random(777);
        int Roll() => 15 + (int)(rng.NextDouble() * 80);
        var batters = Enumerable.Range(0, 9)
            .Select(i => P($"選手{i}", contact: Roll(), power: Roll(), discipline: Roll(), speed: Roll(), bunt: Roll()))
            .ToList();

        var baseline = LineupOrderer.Arrange(batters, C).Select(p => p.Name);
        Assert.Equal(baseline, LineupOrderer.Arrange(batters, C, SchoolStyle.Standard).Select(p => p.Name));
        Assert.Equal(baseline, LineupOrderer.Arrange(batters, C, SchoolStyle.TotalBaseball).Select(p => p.Name));
    }

    private static Player Bat(string name, Handedness bats, int contact = 50, int bunt = 50)
        => P(name, contact: contact, bunt: bunt) with { Bats = bats };

    [Fact]
    public void Arrange_BalanceHands_BreaksUpSameHandedNeighbors_WhenAbilitiesTie()
    {
        // 能力が全員同じ（同点は入力順）。右右右左右…の並びで、2番に左打者が入ると隣が逆打ちになる。
        var batters = new List<Player>
        {
            Bat("右0", Handedness.Right), Bat("右1", Handedness.Right), Bat("右2", Handedness.Right),
            Bat("左3", Handedness.Left), Bat("右4", Handedness.Right), Bat("右5", Handedness.Right),
            Bat("右6", Handedness.Right), Bat("右7", Handedness.Right), Bat("右8", Handedness.Right),
        };

        Assert.Equal("右1", LineupOrderer.Arrange(batters, C)[1].Name);
        Assert.Equal("右1", LineupOrderer.Arrange(batters, C, balanceHands: false)[1].Name);
        Assert.Equal("左3", LineupOrderer.Arrange(batters, C, balanceHands: true)[1].Name);
    }

    [Fact]
    public void Arrange_BalanceHands_SwitchHitterCountsAsBalancing()
    {
        var batters = new List<Player>
        {
            Bat("右0", Handedness.Right), Bat("右1", Handedness.Right), Bat("右2", Handedness.Right),
            Bat("両3", Handedness.Switch), Bat("右4", Handedness.Right), Bat("右5", Handedness.Right),
            Bat("右6", Handedness.Right), Bat("右7", Handedness.Right), Bat("右8", Handedness.Right),
        };

        Assert.Equal("両3", LineupOrderer.Arrange(batters, C, balanceHands: true)[1].Name);
    }

    [Fact]
    public void Arrange_BalanceHands_DoesNotOverrideClearAbilityGap()
    {
        // 2番は右の小技巧者が左の凡庸な打者に勝つ（ボーナスは同程度の打者の間の決め手にとどまる）。
        var batters = NineWith(
            Bat("1番", Handedness.Right, contact: 90),
            Bat("右の小技巧者", Handedness.Right, contact: 90, bunt: 90),
            Bat("左の凡庸", Handedness.Left, contact: 50, bunt: 50));
        batters[0] = batters[0] with { Discipline = 95, Speed = 95 };

        var result = LineupOrderer.Arrange(batters, C, balanceHands: true);

        Assert.Equal("右の小技巧者", result[1].Name);
    }

    [Fact]
    public void Compose_BalancesHandedness_ForAiTeams()
    {
        var fielders = new[]
        {
            Handedness.Right, Handedness.Right, Handedness.Right, Handedness.Right,
            Handedness.Left, Handedness.Left, Handedness.Left, Handedness.Left,
        }.Select((h, i) => Bat($"野手{i}", h)).ToList();
        var pitcher = Pitcher("非力投手", contact: 30, power: 30);
        var team = BuildTeam(pitcher, fielders, new List<Player>());

        int SameHandedPairs(IReadOnlyList<Player> o)
            => Enumerable.Range(1, 7).Count(i => o[i - 1].Bats == o[i].Bats);

        var plain = LineupOrderer.Arrange(fielders, C);
        var composed = LineupOrderer.Compose(team, C, modernRules: null, calendarYear: null);

        Assert.True(SameHandedPairs(composed.BattingOrder) < SameHandedPairs(plain),
            $"Compose は同じ打ち手の連続を減らすべき: {SameHandedPairs(composed.BattingOrder)} vs {SameHandedPairs(plain)}");
    }
}

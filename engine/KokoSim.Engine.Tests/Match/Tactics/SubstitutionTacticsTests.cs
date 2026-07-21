using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 選手交代の采配（設計書09 §6, C-2）。StandardTacticsBrain の判断（決定論）と、
/// 実試合への配線（控え非空＋Brainありのときだけ交代が起きる＝既定オフで従来一致）を検証。
/// 代打判断の調子込み評価とオーダー編成（設計書11 §4, issue #48）もここで扱う。
/// </summary>
public sealed class SubstitutionTacticsTests
{
    private static readonly IRandomSource Rng = new Xoshiro256Random(1);

    private static Player P(string name, int contact = 50, int speed = 50, int fielding = 50)
        => new() { Name = name, Contact = contact, Speed = speed, Fielding = fielding };

    // ===== StandardTacticsBrain の判断（既定係数, 決定論） =====

    [Fact]
    public void PinchHit_LateCloseGame_ReplacesWeakBatterWithBetterBench()
    {
        var brain = new StandardTacticsBrain();
        var weak = P("非力", contact: 32);
        var bench = new List<Player> { P("代打の切り札", contact: 88), P("凡庸", contact: 40) };
        var sit = new SubstitutionSituation(8, 9, 0, 0, null, null, null, weak, false,
            new List<Player> { weak }, bench);

        Assert.Equal("代打の切り札", brain.CallPinchHit(sit, Rng)!.Name);
    }

    [Fact]
    public void PinchHit_NotTriggered_EarlyInning_OrForPitcher_OrBlowout()
    {
        var brain = new StandardTacticsBrain();
        var weak = P("非力", contact: 32);
        var bench = new List<Player> { P("切り札", contact: 90) };
        var lineup = new List<Player> { weak };

        Assert.Null(brain.CallPinchHit(new SubstitutionSituation(3, 9, 0, 0, null, null, null, weak, false, lineup, bench), Rng)); // 序盤
        Assert.Null(brain.CallPinchHit(new SubstitutionSituation(8, 9, 0, 0, null, null, null, weak, true, lineup, bench), Rng));  // 投手枠
        Assert.Null(brain.CallPinchHit(new SubstitutionSituation(8, 9, 0, -8, null, null, null, weak, false, lineup, bench), Rng)); // 大差
    }

    [Fact]
    public void PinchHit_NotTriggered_WhenBenchNotClearlyBetter()
    {
        var brain = new StandardTacticsBrain();
        var starter = P("そこそこ", contact: 44);
        var bench = new List<Player> { P("僅かに上", contact: 50) }; // +6 < 既定 improvement 12
        var sit = new SubstitutionSituation(8, 9, 0, 0, null, null, null, starter, false,
            new List<Player> { starter }, bench);
        Assert.Null(brain.CallPinchHit(sit, Rng));
    }

    // ===== 代打判断の調子込み評価（設計書11 §4「代打」, issue #48） =====

    [Fact]
    public void PinchHit_TerribleConditionBatter_BecomesEligibleBelowCeiling()
    {
        // 生ミート50はケイリング(46)超で本来は代打対象にすらならないが、絶不調(-2段階×2.5=-5)で
        // 実効45まで下がりケイリングを割り込む＝絶不調の好打者が代打の対象になる。
        var brain = new StandardTacticsBrain();
        var slumping = P("絶不調の主砲", contact: 50) with { Condition = Condition.Terrible };
        var bench = new List<Player> { P("代打", contact: 90) };
        var sit = new SubstitutionSituation(8, 9, 0, 0, null, null, null, slumping, false,
            new List<Player> { slumping }, bench);

        Assert.Equal("代打", brain.CallPinchHit(sit, Rng)!.Name);
    }

    [Fact]
    public void PinchHit_NormalConditionBatter_AboveCeiling_NotReplaced()
    {
        // 対照: 同じ生ミートでも通常の調子ならケイリング超のままで代打は出ない（回帰確認）。
        var brain = new StandardTacticsBrain();
        var normal = P("好調", contact: 50);
        var bench = new List<Player> { P("代打", contact: 90) };
        var sit = new SubstitutionSituation(8, 9, 0, 0, null, null, null, normal, false,
            new List<Player> { normal }, bench);

        Assert.Null(brain.CallPinchHit(sit, Rng));
    }

    [Fact]
    public void PinchHit_ExcellentConditionBench_TipsMarginalCandidateIntoSelection()
    {
        // 控え(生40)は通常の調子なら +8 < 既定improvement(12) で選ばれないが、絶好調(+2段階×2.5=+5)で
        // 実効45となり 32+12=44 を上回る＝絶好調の控えが代打に選ばれる。
        var brain = new StandardTacticsBrain();
        var weak = P("非力", contact: 32);
        var marginal = P("控え", contact: 40) with { Condition = Condition.Excellent };
        var bench = new List<Player> { marginal };
        var sit = new SubstitutionSituation(8, 9, 0, 0, null, null, null, weak, false,
            new List<Player> { weak }, bench);

        Assert.Equal("控え", brain.CallPinchHit(sit, Rng)!.Name);
    }

    [Fact]
    public void PinchHit_NormalConditionBench_SameRawContact_NotSelected()
    {
        // 対照: 同じ生ミートでも通常の調子なら improvement を満たさず選ばれない（回帰確認）。
        var brain = new StandardTacticsBrain();
        var weak = P("非力", contact: 32);
        var marginal = P("控え", contact: 40);
        var bench = new List<Player> { marginal };
        var sit = new SubstitutionSituation(8, 9, 0, 0, null, null, null, weak, false,
            new List<Player> { weak }, bench);

        Assert.Null(brain.CallPinchHit(sit, Rng));
    }

    // ===== オーダー編成: StandardTacticsBrain（設計書11 §4, issue #48） =====

    [Fact]
    public void ComposeBattingOrder_MovesTerribleConditionBatterDown_KeepsPositionAndRelativeOrder()
    {
        var brain = new StandardTacticsBrain();
        var order = new List<Player>
        {
            P("1番", contact: 50) with { Position = FieldPosition.Catcher },
            P("2番", contact: 50) with { Position = FieldPosition.FirstBase },
            P("3番", contact: 50) with { Position = FieldPosition.SecondBase },
            P("4番絶不調", contact: 80) with { Position = FieldPosition.ThirdBase, Condition = Condition.Terrible },
            P("5番", contact: 50) with { Position = FieldPosition.Shortstop },
        };

        var result = brain.ComposeBattingOrder(order);

        Assert.Equal("4番絶不調", result[^1].Name);                 // 最下位へ下がる
        Assert.Equal(FieldPosition.ThirdBase, result[^1].Position); // 守備位置は変わらない
        // 安定ソート: 絶不調以外は元の相対順のまま。
        Assert.Equal(new[] { "1番", "2番", "3番", "5番" }, result.Take(4).Select(p => p.Name));
    }

    [Fact]
    public void ComposeBattingOrder_AllNormalCondition_OrderUnchanged()
    {
        var brain = new StandardTacticsBrain();
        var order = new List<Player> { P("1番"), P("2番"), P("3番") };

        var result = brain.ComposeBattingOrder(order);

        Assert.Equal(order.Select(p => p.Name), result.Select(p => p.Name));
    }

    // ===== オーダー編成: AiTacticsBrain（③豪腕依存は据え置き・①戦術眼で気づく確率が変わる） =====

    private static List<Player> MixedConditionOrder()
    {
        var positions = new[]
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
            FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher,
        };
        var list = new List<Player>();
        for (var i = 0; i < positions.Length; i++)
        {
            var cond = i switch { 3 => Condition.Terrible, 6 => Condition.Excellent, _ => Condition.Normal };
            list.Add(P($"打順{i + 1}") with { Position = positions[i], Condition = cond });
        }
        return list;
    }

    [Fact]
    public void ComposeBattingOrder_AiTacticsBrain_AceDependent_NeverReorders()
    {
        var order = MixedConditionOrder();
        var brain = new AiTacticsBrain(new AiProfile(TacticalSense: 95, TierRank: 7, SchoolStyle.AceDependent));
        for (ulong i = 0; i < 100; i++)
        {
            var result = brain.ComposeBattingOrder(order, new Xoshiro256Random(i));
            Assert.Equal(order.Select(p => p.Name), result.Select(p => p.Name));
        }
    }

    [Fact]
    public void ComposeBattingOrder_AiTacticsBrain_HighSense_ReordersMoreOftenThanLowSense()
    {
        var order = MixedConditionOrder();
        int Reorders(int sense)
        {
            var brain = new AiTacticsBrain(new AiProfile(sense, TierRank: 7, SchoolStyle.Standard));
            var n = 0;
            for (ulong i = 0; i < 300; i++)
            {
                var result = brain.ComposeBattingOrder(order, new Xoshiro256Random(i));
                if (!result.Select(p => p.Name).SequenceEqual(order.Select(p => p.Name))) n++;
            }
            return n;
        }
        Assert.True(Reorders(95) > Reorders(10), "戦術眼が高いほど調子を反映した並べ替えを行うはず");
    }

    [Fact]
    public void PinchRun_LateCloseGame_ReplacesSlowRunnerInScoringPosition()
    {
        var brain = new StandardTacticsBrain();
        var slow = P("鈍足", speed: 30);
        var bench = new List<Player> { P("俊足代走", speed: 92) };
        var sit = new SubstitutionSituation(8, 9, 1, 0, null, slow, null, P("打者"), false,
            new List<Player> { slow }, bench);

        var pr = brain.CallPinchRun(sit, Rng);
        Assert.NotNull(pr);
        Assert.Equal("鈍足", pr!.Value.Runner.Name);
        Assert.Equal("俊足代走", pr.Value.Sub.Name);
    }

    [Fact]
    public void DefensiveSub_LateWithLead_ReplacesWeakFielder()
    {
        var brain = new StandardTacticsBrain();
        var weakGlove = P("守備難", fielding: 30) with { Position = FieldPosition.LeftField };
        var pitcher = P("投手", fielding: 20) with { Position = FieldPosition.Pitcher };
        var lineup = new List<Player> { weakGlove, pitcher, P("普通", fielding: 60) };
        var bench = new List<Player> { P("守備固め", fielding: 85) };

        // 守備側リード（ScoreDiff は守備側視点で +2）。
        var sit = new SubstitutionSituation(8, 9, 0, 2, null, null, null, P("相手打者"), false, lineup, bench);
        var d = brain.CallDefensiveSub(sit, Rng);
        Assert.NotNull(d);
        Assert.Equal("守備難", d!.Value.Out.Name);   // 投手(20)は除外され、次に低い守備難(30)が対象
        Assert.Equal("守備固め", d.Value.Sub.Name);
    }

    [Fact]
    public void DefensiveSub_NotTriggered_WhenTrailing()
    {
        var brain = new StandardTacticsBrain();
        var weakGlove = P("守備難", fielding: 30) with { Position = FieldPosition.LeftField };
        var sit = new SubstitutionSituation(8, 9, 0, -1, null, null, null, P("相手打者"), false,
            new List<Player> { weakGlove }, new List<Player> { P("守備固め", fielding: 90) });
        Assert.Null(brain.CallDefensiveSub(sit, Rng));
    }

    // ===== 実試合への配線 =====

    private static Team WeakTeamWithBench(string name, bool withBench, ITacticsBrain? brain)
    {
        var positions = new[]
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
            FieldPosition.CenterField, FieldPosition.RightField,
        };
        var order = positions.Select(p => P($"{name}{p}弱", contact: 30, speed: 30, fielding: 30) with { Position = p })
            .ToList();
        order.Add(P($"{name}P", contact: 20) with { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage });
        var bench = withBench
            ? new List<Player>
            {
                P($"{name}代打", contact: 95) with { Position = FieldPosition.FirstBase },
                P($"{name}代走", speed: 95) with { Position = FieldPosition.SecondBase },
                P($"{name}守備", fielding: 95) with { Position = FieldPosition.Shortstop },
            }
            : new List<Player>();
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bench = bench, Tactics = brain,
        };
    }

    /// <summary>控え非空＋Brainありなら実試合で交代が発生する（積極係数のBrainで確実に）。</summary>
    [Fact]
    public void Wiring_BenchAndBrain_ProducesSubstitutionsInRealGame()
    {
        // 常に代打を検討する係数（序盤から・僅差不問・僅差でなくても）。
        var eager = new TacticsCoefficients
        {
            PinchHitFromInning = 1, PinchHitContactCeiling = 80, PinchHitImprovement = 5,
            PinchHitMinDiff = -30, PinchHitMaxDiff = 30,
        };
        var brain = new StandardTacticsBrain(eager);
        var away = WeakTeamWithBench("遠征", withBench: true, brain);
        var home = WeakTeamWithBench("地元", withBench: true, brain);

        var total = 0;
        for (ulong s = 0; s < 5; s++)
        {
            var r = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(s));
            total += r.AwaySubstitutions + r.HomeSubstitutions;
        }
        Assert.True(total > 0, "控え＋Brainありでも実試合で交代が一度も起きていない");
    }

    /// <summary>控えが空なら、Brainがあっても交代は起きない（既定オフ＝従来挙動と一致する条件）。</summary>
    [Fact]
    public void Wiring_EmptyBench_NeverSubstitutes()
    {
        var brain = new StandardTacticsBrain();
        var away = WeakTeamWithBench("遠征", withBench: false, brain);
        var home = WeakTeamWithBench("地元", withBench: false, brain);

        var r = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(7));
        Assert.Equal(0, r.AwaySubstitutions);
        Assert.Equal(0, r.HomeSubstitutions);
    }

    [Fact]
    public void Wiring_IsDeterministic()
    {
        var brain = new StandardTacticsBrain(new TacticsCoefficients
        {
            PinchHitFromInning = 1, PinchHitContactCeiling = 80, PinchHitImprovement = 5,
            PinchHitMinDiff = -30, PinchHitMaxDiff = 30,
        });
        var away = WeakTeamWithBench("遠征", withBench: true, brain);
        var home = WeakTeamWithBench("地元", withBench: true, brain);

        var r1 = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(42));
        var r2 = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(42));
        Assert.Equal(r1.AwayRuns, r2.AwayRuns);
        Assert.Equal(r1.HomeRuns, r2.HomeRuns);
        Assert.Equal(r1.HomeSubstitutions, r2.HomeSubstitutions);
        Assert.Equal(r1.AwaySubstitutions, r2.AwaySubstitutions);
    }
}

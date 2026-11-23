using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// プレッシャー指数と精神力補正（設計書02 §3.1-3.2）。
/// 指数の加点表・補正式の既知値（精神力100・P8で+16% / 20・P8で−9.6%）・
/// 精神力50の恒等性（既存統計帯の不変条件）・緩和と疲労増幅の非対称性を検証する。
/// </summary>
public sealed class PressureTests
{
    private static readonly PressureCoefficients C = new();

    // ===== 士気の土台（設計書04 §1.2・issue #1270 M1） =====

    [Theory]
    [InlineData(null, 0.0)]
    [InlineData(29.9, -0.10)]
    [InlineData(30.0, 0.0)]
    [InlineData(69.9, 0.0)]
    [InlineData(70.0, 0.10)]
    [InlineData(100.0, 0.10)]
    public void MoraleMitigation_ByTrust(double? morale, double expected)
        => Assert.Equal(expected, PressureModel.MoraleMitigation(morale, C), 9);

    [Fact]
    public void Morale_MentalFifty_IsAlwaysIdentity()
    {
        foreach (var m in new[] { -0.10, 0.0, 0.10 })
            Assert.Equal(1.0, PressureModel.Multiplier(50, 8, C, moraleMitigation: m));
    }

    [Fact]
    public void Morale_ShrinksOrGrowsOnlyTheNegativeSide()
    {
        // 精神力20・P8: raw = −0.6×0.02×8 = −0.096。
        var plain = PressureModel.Multiplier(20, 8, C);
        Assert.Equal(1.0 - 0.096, plain, 9);
        Assert.Equal(1.0 - 0.096 * 0.9, PressureModel.Multiplier(20, 8, C, moraleMitigation: 0.10), 9);
        Assert.Equal(1.0 - 0.096 * 1.1, PressureModel.Multiplier(20, 8, C, moraleMitigation: -0.10), 9);
        // 伝令の緩和（0.5）と足し合わせる。
        Assert.Equal(1.0 - 0.096 * 0.4, PressureModel.Multiplier(20, 8, C, mitigation: 0.5, moraleMitigation: 0.10), 9);
        // 正側（精神力の高い選手）には効かない。
        Assert.Equal(PressureModel.Multiplier(90, 8, C), PressureModel.Multiplier(90, 8, C, moraleMitigation: -0.10));
    }

    [Fact]
    public void Morale_Zero_IsBitIdenticalToTheOldFormula()
    {
        foreach (var mental in new[] { 1, 20, 49, 50, 51, 80, 99 })
        foreach (var p in new[] { 0, 3, 8 })
        foreach (var mit in new[] { 0.0, 0.3, 1.2 })
            Assert.Equal(PressureModel.Multiplier(mental, p, C, mit, true, 1.3),
                PressureModel.Multiplier(mental, p, C, mit, true, 1.3, moraleMitigation: 0.0));
    }

    [Fact]
    public void Compute_PracticeGameEarlyNoRunners_IsZero()
    {
        var s = new PressureSituation(0, 1, 5, false, false, false, false);
        Assert.Equal(0, PressureModel.Compute(s, C));
    }

    [Fact]
    public void Compute_KoshienFinal_LateCloseBasesLoadedRetirement_IsMax()
    {
        // 決勝(+3) + 8回以降(+1) + 2点差以内(+1) + 満塁(+2) + 負けたら引退(+1) = 8。
        var s = new PressureSituation(3, 9, 1, true, true, true, true);
        Assert.Equal(8, PressureModel.Compute(s, C));
    }

    [Theory]
    [InlineData(8, 0, 1)]   // 8回以降 +1
    [InlineData(7, 0, 0)]   // 7回はまだ
    public void Compute_LateInningPoint(int inning, int stage, int expected)
    {
        var s = new PressureSituation(stage, inning, 10, false, false, false, false);
        Assert.Equal(expected, PressureModel.Compute(s, C));
    }

    [Fact]
    public void Compute_Risp_PlusOne_ButBasesLoadedPlusTwo()
    {
        var risp = new PressureSituation(0, 1, 10, true, false, false, false);
        var loaded = new PressureSituation(0, 1, 10, true, true, true, false);
        Assert.Equal(1, PressureModel.Compute(risp, C));
        Assert.Equal(2, PressureModel.Compute(loaded, C));
    }

    // --- 補正式の既知値（設計書02 §3.2 の記載値そのまま） ---

    [Fact]
    public void Multiplier_Clutch100AtP8_Plus16Percent()
        => Assert.Equal(1.16, PressureModel.Multiplier(100, 8, C), 9);

    [Fact]
    public void Multiplier_Nervous20AtP8_Minus9_6Percent()
        => Assert.Equal(1.0 - 0.096, PressureModel.Multiplier(20, 8, C), 9);

    [Fact]
    public void Multiplier_Mental50_AlwaysIdentity()
    {
        for (var p = 0; p <= 8; p++)
        {
            Assert.Equal(1.0, PressureModel.Multiplier(50, p, C));
        }
    }

    [Fact]
    public void Multiplier_MitigationRelievesNegativeOnly()
    {
        // 負側: 緩和で1.0へ近づく。
        var choke = PressureModel.Multiplier(20, 8, C);
        var relieved = PressureModel.Multiplier(20, 8, C, mitigation: 0.5);
        Assert.True(relieved > choke);
        Assert.True(relieved < 1.0);
        // 正側: 緩和は効かない（クラッチは削らない）。
        Assert.Equal(PressureModel.Multiplier(100, 8, C),
                     PressureModel.Multiplier(100, 8, C, mitigation: 0.5), 9);
    }

    [Fact]
    public void Multiplier_FatigueAmplifiesNegativeOnly()
    {
        var fresh = PressureModel.Multiplier(20, 8, C);
        var gassed = PressureModel.Multiplier(20, 8, C, fatigueOver: true);
        Assert.True(gassed < fresh); // 負側がさらに沈む（終盤の失点劇）
        // 正側は増幅されない。
        Assert.Equal(PressureModel.Multiplier(100, 8, C),
                     PressureModel.Multiplier(100, 8, C, fatigueOver: true), 9);
    }

    // --- 適用（補正係数方式。倍率1.0なら参照ごと恒等） ---

    [Fact]
    public void Apply_IdentityAtMultiplierOne()
    {
        var b = new BatterAttributes { Contact = 72 };
        var p = PitcherAttributes.LeagueAverage;
        Assert.Same(b, PressureModel.ApplyBatter(b, 1.0));
        Assert.Same(p, PressureModel.ApplyPitcher(p, 1.0));
    }

    [Fact]
    public void Apply_ScalesContactAndControl()
    {
        var b = PressureModel.ApplyBatter(new BatterAttributes { Contact = 80 }, 1.10);
        Assert.Equal(88, b.Contact);
        var p = PressureModel.ApplyPitcher(new PitcherAttributes { Control = 60 }, 0.90);
        Assert.Equal(54, p.Control);
    }

    // --- 試合エンジン統合: 精神力50の既定チームでは結果が一切変わらない ---

    [Fact]
    public void Game_DefaultMental_PressureContextDoesNotChangeOutcome()
    {
        var plain = new GameContext();
        var koshien = new GameContext { PressureStageBonus = 3, RetirementOnLine = true };
        for (ulong s = 0; s < 5; s++)
        {
            var a = GameEngine.Play(TeamOf("A"), TeamOf("H"), plain, new Engine.Core.Xoshiro256Random(s));
            var b = GameEngine.Play(TeamOf("A"), TeamOf("H"), koshien, new Engine.Core.Xoshiro256Random(s));
            Assert.Equal(a.AwayRuns, b.AwayRuns);
            Assert.Equal(a.HomeRuns, b.HomeRuns);
            Assert.Equal(a.TotalPitches, b.TotalPitches);
        }
    }

    private static Team TeamOf(string name)
    {
        var order = new System.Collections.Generic.List<Player>();
        var positions = new[]
        {
            Engine.Match.Field.FieldPosition.Catcher, Engine.Match.Field.FieldPosition.FirstBase,
            Engine.Match.Field.FieldPosition.SecondBase, Engine.Match.Field.FieldPosition.ThirdBase,
            Engine.Match.Field.FieldPosition.Shortstop, Engine.Match.Field.FieldPosition.LeftField,
            Engine.Match.Field.FieldPosition.CenterField, Engine.Match.Field.FieldPosition.RightField,
        };
        foreach (var pos in positions) order.Add(new Player { Position = pos });
        order.Add(new Player
        {
            Position = Engine.Match.Field.FieldPosition.Pitcher,
            Name = name + "P",
            Pitching = PitcherAttributes.LeagueAverage,
        });
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }
}

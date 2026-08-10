using System.Collections.Generic;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 練習道具（分野別の成長倍率）の検証。施設の効率係数が全メニュー一律なのに対し、
/// 道具は「振っているメニューの分野」にだけ効く（設計書03 §3.1 / 04 §1.4 備品）。
/// 未所持（<see cref="EquipmentBonus.None"/>／null）で従来と1ビット一致すること（不変条件#2）が要。
/// </summary>
public sealed class TrainingEquipmentTests
{
    private const int Ref = 300;

    private static DevelopingPlayer NewPlayer()
    {
        var p = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(p, false);
        foreach (var k in AbilityKinds.All)
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }
        return p;
    }

    // --- 1. 分野の写像: 全メニューがちょうど1つの分野に属する（休養だけ分野なし） ---

    [Theory]
    [InlineData(TrainingMenu.Batting, TrainingField.Batting)]
    [InlineData(TrainingMenu.PowerHitting, TrainingField.Batting)]
    [InlineData(TrainingMenu.PlateDiscipline, TrainingField.Batting)]
    [InlineData(TrainingMenu.Strength, TrainingField.Batting)]
    [InlineData(TrainingMenu.Defense, TrainingField.Defense)]
    [InlineData(TrainingMenu.Throwing, TrainingField.Defense)]
    [InlineData(TrainingMenu.DefenseSS, TrainingField.Defense)]
    [InlineData(TrainingMenu.DefenseInfield, TrainingField.Defense)]
    [InlineData(TrainingMenu.DefenseOutfield, TrainingField.Defense)]
    [InlineData(TrainingMenu.BaseRunning, TrainingField.BaseRunning)]
    [InlineData(TrainingMenu.Running, TrainingField.BaseRunning)]
    [InlineData(TrainingMenu.Bunt, TrainingField.BaseRunning)]
    [InlineData(TrainingMenu.Pitching, TrainingField.Pitching)]
    [InlineData(TrainingMenu.BreakingBall, TrainingField.Pitching)]
    [InlineData(TrainingMenu.VelocityTraining, TrainingField.Pitching)]
    public void Of_MapsMenuToField(TrainingMenu menu, TrainingField expected)
        => Assert.Equal(expected, TrainingFields.Of(menu));

    [Fact]
    public void Of_Rest_HasNoField()
        => Assert.Null(TrainingFields.OfOrNull(TrainingMenu.Rest));

    /// <summary>休養以外の全メニューが分野を持つ（メニューを足したときの取りこぼし防止）。</summary>
    [Fact]
    public void EveryMenuExceptRest_HasField()
    {
        foreach (TrainingMenu menu in System.Enum.GetValues(typeof(TrainingMenu)))
        {
            if (menu == TrainingMenu.Rest) continue;
            Assert.True(TrainingFields.OfOrNull(menu).HasValue, $"{menu} に分野が割り当てられていない");
        }
    }

    // --- 2. 未所持は従来と1ビット一致（不変条件#2） ---

    [Fact]
    public void NoEquipment_MatchesLegacyExactly()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var alloc = new[] { new MenuAllocation(TrainingMenu.Batting, Ref) };

        var legacy = NewPlayer();
        var none = NewPlayer();
        var explicitNone = NewPlayer();
        for (var i = 0; i < 12; i++)
        {
            DevelopmentModel.TrainWeekPlan(legacy, alloc, Ref, 0, 1.0, stages, c);
            DevelopmentModel.TrainWeekPlan(none, alloc, Ref, 0, 1.0, stages, c, equipment: null);
            DevelopmentModel.TrainWeekPlan(explicitNone, alloc, Ref, 0, 1.0, stages, c,
                equipment: EquipmentBonus.None);
        }

        foreach (var k in AbilityKinds.All)
        {
            Assert.Equal(legacy.Exp(k), none.Exp(k), 9);
            Assert.Equal(legacy.Exp(k), explicitNone.Exp(k), 9);
            Assert.Equal(legacy.Level(k), none.Level(k));
            Assert.Equal(legacy.Level(k), explicitNone.Level(k));
        }
    }

    // --- 3. 道具は自分の分野にだけ効く ---

    [Fact]
    public void BattingEquipment_BoostsBattingOnly()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var gear = new EquipmentBonus { Batting = 1.12 };

        // 打撃メニュー: 道具ありのほうが伸びる。
        var plainBat = NewPlayer();
        var gearBat = NewPlayer();
        // 投手メニュー: 打撃道具では変わらない。
        var plainPitch = NewPlayer();
        var gearPitch = NewPlayer();

        var batAlloc = new[] { new MenuAllocation(TrainingMenu.Batting, Ref) };
        var pitchAlloc = new[] { new MenuAllocation(TrainingMenu.Pitching, Ref) };
        for (var i = 0; i < 12; i++)
        {
            DevelopmentModel.TrainWeekPlan(plainBat, batAlloc, Ref, 0, 1.0, stages, c);
            DevelopmentModel.TrainWeekPlan(gearBat, batAlloc, Ref, 0, 1.0, stages, c, equipment: gear);
            DevelopmentModel.TrainWeekPlan(plainPitch, pitchAlloc, Ref, 0, 1.0, stages, c);
            DevelopmentModel.TrainWeekPlan(gearPitch, pitchAlloc, Ref, 0, 1.0, stages, c, equipment: gear);
        }

        Assert.True(TotalExp(gearBat) > TotalExp(plainBat), "打撃道具で打撃メニューが伸びていない");
        Assert.Equal(TotalExp(plainPitch), TotalExp(gearPitch), 9);
    }

    /// <summary>倍率は配分スケールと同じ位置に掛かる＝道具1.12は配分1.12倍と同じ伸び。</summary>
    [Fact]
    public void EquipmentFactor_IsEquivalentToScalingMinutes()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();

        var byGear = NewPlayer();
        var byMinutes = NewPlayer();
        DevelopmentModel.TrainWeekPlan(byGear,
            new[] { new MenuAllocation(TrainingMenu.Defense, Ref) }, Ref, 0, 1.0, stages, c,
            equipment: new EquipmentBonus { Defense = 1.20 });
        DevelopmentModel.TrainWeekPlan(byMinutes,
            new[] { new MenuAllocation(TrainingMenu.Defense, (int)(Ref * 1.20)) }, Ref, 0, 1.0, stages, c);

        foreach (var k in AbilityKinds.All)
            Assert.Equal(byMinutes.Exp(k), byGear.Exp(k), 9);
    }

    /// <summary>守備道具はポジション別守備（適性のみのメニュー）にも効く。</summary>
    [Fact]
    public void DefenseEquipment_BoostsPositionAptitudeTraining()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var alloc = new[] { new MenuAllocation(TrainingMenu.DefenseSS, Ref) };

        var plain = NewPlayer();
        var gear = NewPlayer();
        for (var i = 0; i < 12; i++)
        {
            DevelopmentModel.TrainWeekPlan(plain, alloc, Ref, 0, 1.0, stages, c);
            DevelopmentModel.TrainWeekPlan(gear, alloc, Ref, 0, 1.0, stages, c,
                equipment: new EquipmentBonus { Defense = 1.12 });
        }

        // 適性はレベルが上がると exp が繰り越されるので、レベルと exp を合成して比べる。
        double AptTotal(DevelopingPlayer p)
        {
            var pos = KokoSim.Engine.Match.Field.FieldPosition.Shortstop;
            return p.Aptitude(pos) * 1000.0 + p.AptitudeExp(pos);
        }
        Assert.True(AptTotal(gear) > AptTotal(plain), "守備道具がポジション別守備の適性に効いていない");
    }

    // --- 4. 集約: 分野ごとに独立して引ける ---

    [Fact]
    public void None_IsAllOnes()
    {
        foreach (TrainingField f in System.Enum.GetValues(typeof(TrainingField)))
            Assert.Equal(1.0, EquipmentBonus.None.For(f));
    }

    private static double TotalExp(DevelopingPlayer p)
    {
        double sum = 0;
        foreach (var k in AbilityKinds.All) sum += p.Level(k) * 1000.0 + p.Exp(k);
        return sum;
    }
}

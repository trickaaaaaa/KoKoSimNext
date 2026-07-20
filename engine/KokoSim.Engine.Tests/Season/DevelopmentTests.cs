using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>育成モデルの検証（設計書02 §5）。成長段階の形・才能上限・必要exp曲線が設計通りか。</summary>
public sealed class DevelopmentTests
{
    // --- 成長段階の生涯獲得量（設計書02 §5.2 の設計値） ---

    [Theory]
    [InlineData(GrowthType.Early, 28.8)]
    [InlineData(GrowthType.Standard, 29.2)]
    [InlineData(GrowthType.Late, 31.2)]
    public void LifetimeTotal_MatchesDesignValues(GrowthType type, double expected)
    {
        var table = new GrowthStageTable();
        Assert.Equal(expected, table.LifetimeTotal(type), 6);
    }

    [Fact]
    public void LateBloomer_HasHighestLifetimeTotal()
    {
        var t = new GrowthStageTable();
        Assert.True(t.LifetimeTotal(GrowthType.Late) > t.LifetimeTotal(GrowthType.Standard));
        Assert.True(t.LifetimeTotal(GrowthType.Standard) > t.LifetimeTotal(GrowthType.Early));
    }

    // --- 必要exp曲線（設計書02 §5.1: 100×1.05^v） ---

    [Theory]
    [InlineData(0, 100.0)]
    [InlineData(20, 265.33)]
    [InlineData(50, 1146.74)]
    public void RequiredExp_FollowsFormula(int level, double expected)
    {
        var c = new TrainingCoefficients();
        Assert.Equal(expected, c.RequiredExp(level), 1);
    }

    // --- 成長段階の形（早熟=前半厚い / 晩成=後半厚い） ---

    private static int LevelsGainedInStage(GrowthType type, int stageIndex, int weeks)
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer { GrowthType = type };
        p.SetLevel(AbilityKind.Contact, 30);
        p.SetCap(AbilityKind.Contact, 99);
        for (var i = 0; i < weeks; i++)
        {
            DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, stageIndex, 1.0, stages, c);
        }
        return p.Level(AbilityKind.Contact) - 30;
    }

    [Fact]
    public void EarlyBloomer_GainsMoreEarly_ThanLate()
    {
        var earlyStage0 = LevelsGainedInStage(GrowthType.Early, 0, 20); // 1年前半 ×1.4
        var earlyStage4 = LevelsGainedInStage(GrowthType.Early, 4, 20); // 3年 ×0.6
        Assert.True(earlyStage0 > earlyStage4, $"早熟 前半{earlyStage0} vs 3年{earlyStage4}");
    }

    [Fact]
    public void LateBloomer_GainsMoreLate_ThanEarly()
    {
        var lateStage0 = LevelsGainedInStage(GrowthType.Late, 0, 20); // ×0.6
        var lateStage4 = LevelsGainedInStage(GrowthType.Late, 4, 20); // ×1.8
        Assert.True(lateStage4 > lateStage0, $"晩成 前半{lateStage0} vs 3年{lateStage4}");
    }

    [Fact]
    public void LateBloomer_SurgesMoreThanEarlyBloomer_InFinalStage()
    {
        var earlyFinal = LevelsGainedInStage(GrowthType.Early, 4, 20);
        var lateFinal = LevelsGainedInStage(GrowthType.Late, 4, 20);
        Assert.True(lateFinal > earlyFinal, $"3年成長 早熟{earlyFinal} < 晩成{lateFinal}");
    }

    // --- 才能上限で停止 ---

    [Fact]
    public void Growth_StopsAtPotentialCap()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer { GrowthType = GrowthType.Standard };
        p.SetLevel(AbilityKind.Contact, 48);
        p.SetCap(AbilityKind.Contact, 50);

        for (var i = 0; i < 200; i++)
        {
            DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 2, 2.5, stages, c);
        }

        Assert.Equal(50, p.Level(AbilityKind.Contact)); // 上限を越えない
    }

    // --- 球速強化: 球速主体＋スタミナ/パワーを少し、コントロールは上げない（設計書03 §3.1） ---

    [Fact]
    public void VelocityTraining_RaisesVelocityStaminaPower_ButNotControl()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer();
        foreach (var k in new[] { AbilityKind.Velocity, AbilityKind.Stamina, AbilityKind.Power, AbilityKind.Control })
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }

        for (var i = 0; i < 30; i++)
        {
            DevelopmentModel.TrainWeek(p, TrainingMenu.VelocityTraining, 0, 1.0, stages, c);
        }

        Assert.True(p.Level(AbilityKind.Velocity) > 30);   // 主効果: 球速は伸びる
        Assert.True(p.Level(AbilityKind.Stamina) > 30);    // 副効果: スタミナも少し伸びる
        Assert.True(p.Level(AbilityKind.Power) > 30);      // 副効果: パワーも少し伸びる
        Assert.Equal(30, p.Level(AbilityKind.Control));    // コントロールは不変
        // 副効果(sub_factor=0.4)は主効果より伸びが小さい。
        Assert.True(p.Level(AbilityKind.Velocity) - 30 > p.Level(AbilityKind.Stamina) - 30);
    }

    // --- バント練習: バント主体＋ミートを少し（設計書03 §3.1） ---

    [Fact]
    public void BuntPractice_RaisesBunt_AndContactSub()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer();
        foreach (var k in new[] { AbilityKind.Bunt, AbilityKind.Contact })
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }

        for (var i = 0; i < 30; i++)
        {
            DevelopmentModel.TrainWeek(p, TrainingMenu.Bunt, 0, 1.0, stages, c);
        }

        Assert.True(p.Level(AbilityKind.Bunt) > 30);        // 主効果: バント
        Assert.True(p.Level(AbilityKind.Contact) > 30);     // 副効果: ミート
        Assert.True(p.Level(AbilityKind.Bunt) - 30 > p.Level(AbilityKind.Contact) - 30);
    }

    // --- 打撃練習の3分割: 弾道・選球眼に成長経路が生まれる（設計書03 §3.1） ---

    [Theory]
    [InlineData(TrainingMenu.Batting, AbilityKind.Contact, AbilityKind.Discipline)]       // ミート打撃
    [InlineData(TrainingMenu.PowerHitting, AbilityKind.Power, AbilityKind.LaunchTendency)] // 長打打撃
    [InlineData(TrainingMenu.PlateDiscipline, AbilityKind.Discipline, AbilityKind.Contact)]// 選球練習
    public void BattingMenus_RaiseMainAndSub(TrainingMenu menu, AbilityKind main, AbilityKind sub)
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer();
        p.SetLevel(main, 30); p.SetCap(main, 99);
        p.SetLevel(sub, 30); p.SetCap(sub, 99);

        for (var i = 0; i < 30; i++)
        {
            DevelopmentModel.TrainWeek(p, menu, 0, 1.0, stages, c);
        }

        Assert.True(p.Level(main) > 30);                    // 主効果
        Assert.True(p.Level(sub) > 30);                     // 副効果
        Assert.True(p.Level(main) - 30 > p.Level(sub) - 30); // 副効果の伸びは主効果より小さい
    }

    // --- 筋力トレ: パワー主体＋肩を少し（筋肉バカ育成, 設計書03 §3.1） ---

    [Fact]
    public void Strength_RaisesPower_AndArmStrengthSub()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer();
        foreach (var k in new[] { AbilityKind.Power, AbilityKind.ArmStrength })
        {
            p.SetLevel(k, 30);
            p.SetCap(k, 99);
        }

        for (var i = 0; i < 30; i++)
        {
            DevelopmentModel.TrainWeek(p, TrainingMenu.Strength, 0, 1.0, stages, c);
        }

        Assert.True(p.Level(AbilityKind.Power) > 30);        // 主効果: パワー
        Assert.True(p.Level(AbilityKind.ArmStrength) > 30);  // 副効果: 肩
        Assert.True(p.Level(AbilityKind.Power) - 30 > p.Level(AbilityKind.ArmStrength) - 30);
    }

    // --- ポジション別守備練習: そのポジションの適性だけ伸びる（設計書03 §3.1） ---

    [Fact]
    public void PositionDefense_RaisesOnlyThatPositionAptitude()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer();
        p.SetLevel(AbilityKind.Fielding, 30);
        p.SetCap(AbilityKind.Fielding, 99);

        for (var i = 0; i < 30; i++)
            DevelopmentModel.TrainWeek(p, TrainingMenu.DefenseSS, 0, 1.0, stages, c);

        Assert.True(p.Aptitude(FieldPosition.Shortstop) > 50);     // 遊撃の適性は伸びる
        Assert.Equal(50, p.Aptitude(FieldPosition.SecondBase));   // 他ポジは不変
        Assert.Equal(50, p.Aptitude(FieldPosition.CenterField));
        Assert.Equal(30, p.Level(AbilityKind.Fielding));          // 守備地力(Fielding)は伸ばさない
    }

    // --- 内野汎用守備: 内野4ポジを薄く全上げ、外野は上がらない（ユーティリティ育成） ---

    [Fact]
    public void InfieldGeneral_RaisesAllInfield_ThinlyNotOutfield()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = new DevelopingPlayer();

        for (var i = 0; i < 40; i++)
            DevelopmentModel.TrainWeek(p, TrainingMenu.DefenseInfield, 0, 1.0, stages, c);

        foreach (var pos in new[] { FieldPosition.FirstBase, FieldPosition.SecondBase,
                                     FieldPosition.ThirdBase, FieldPosition.Shortstop })
            Assert.True(p.Aptitude(pos) > 50, $"{pos} が伸びていない");

        Assert.Equal(50, p.Aptitude(FieldPosition.LeftField));    // 外野は対象外
        Assert.Equal(50, p.Aptitude(FieldPosition.CenterField));

        // 汎用(薄く)は専念より伸びが小さい。
        var focused = new DevelopingPlayer();
        for (var i = 0; i < 40; i++)
            DevelopmentModel.TrainWeek(focused, TrainingMenu.DefenseSS, 0, 1.0, stages, c);
        Assert.True(focused.Aptitude(FieldPosition.Shortstop) > p.Aptitude(FieldPosition.Shortstop));
    }
}

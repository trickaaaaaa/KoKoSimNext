using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 弾道(LaunchTendency)専用メニュー（FlyBallHitting/GroundBallHitting）の育成検証（issue #1166）。
/// 「指定した向きにだけ動く」「必要expは上げ下げ対称」「才能上限（player.Cap）は見ず[1,99]固定」を確認する。
/// 決定論（乱数不使用）。
/// </summary>
public sealed class LaunchTendencyTrainingTests
{
    private static DevelopingPlayer MakePlayer(int launchTendency, int cap = 99)
    {
        var p = new DevelopingPlayer();
        p.SetLevel(AbilityKind.LaunchTendency, launchTendency);
        p.SetCap(AbilityKind.LaunchTendency, cap);
        return p;
    }

    // --- 向き: 指定した向きにだけ動く ---

    [Fact]
    public void FlyBallHitting_MovesLaunchTendencyUp_Only()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = MakePlayer(50);

        for (var week = 0; week < 10; week++)
            DevelopmentModel.TrainWeek(p, TrainingMenu.FlyBallHitting, 0, 1.0, stages, c);

        Assert.True(p.Level(AbilityKind.LaunchTendency) > 50, "フライ打撃で弾道が上がっていない");
    }

    [Fact]
    public void GroundBallHitting_MovesLaunchTendencyDown_Only()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = MakePlayer(50);

        for (var week = 0; week < 10; week++)
            DevelopmentModel.TrainWeek(p, TrainingMenu.GroundBallHitting, 0, 1.0, stages, c);

        Assert.True(p.Level(AbilityKind.LaunchTendency) < 50, "ゴロ打撃で弾道が下がっていない");
    }

    [Fact]
    public void FlyBallHitting_SubEffect_GrowsPower()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = MakePlayer(50);
        p.SetLevel(AbilityKind.Power, 30);
        p.SetCap(AbilityKind.Power, 99);

        for (var week = 0; week < 10; week++)
            DevelopmentModel.TrainWeek(p, TrainingMenu.FlyBallHitting, 0, 1.0, stages, c);

        Assert.True(p.Exp(AbilityKind.Power) > 0 || p.Level(AbilityKind.Power) > 30,
            "フライ打撃の副効果（パワー）が伸びていない");
    }

    [Fact]
    public void GroundBallHitting_SubEffect_GrowsContact()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = MakePlayer(50);
        p.SetLevel(AbilityKind.Contact, 30);
        p.SetCap(AbilityKind.Contact, 99);

        for (var week = 0; week < 10; week++)
            DevelopmentModel.TrainWeek(p, TrainingMenu.GroundBallHitting, 0, 1.0, stages, c);

        Assert.True(p.Exp(AbilityKind.Contact) > 0 || p.Level(AbilityKind.Contact) > 30,
            "ゴロ打撃の副効果（ミート）が伸びていない");
    }

    // --- 止まる位置: 才能上限（Cap）は見ず、表示レンジ[1,99]で止まる ---

    [Fact]
    public void Up_StopsAt99_IgnoringPlayerCap()
    {
        var c = new TrainingCoefficients();
        // Cap を低く（60）設定しても、弾道は見ずに99まで伸びる（issue #1166: 型属性なので才能上限を見ない）。
        var p = MakePlayer(97, cap: 60);

        for (var week = 0; week < 200; week++)
            DevelopmentModel.ApplyLaunchTendencyExp(p, 1_000_000.0, GrowthDirection.Up, c);

        Assert.Equal(99, p.Level(AbilityKind.LaunchTendency));
        Assert.Equal(0.0, p.Exp(AbilityKind.LaunchTendency)); // 上限到達時は余剰expを捨てる
    }

    [Fact]
    public void Down_StopsAt1_IgnoringPlayerCap()
    {
        var c = new TrainingCoefficients();
        var p = MakePlayer(3, cap: 99);

        for (var week = 0; week < 200; week++)
            DevelopmentModel.ApplyLaunchTendencyExp(p, 1_000_000.0, GrowthDirection.Down, c);

        Assert.Equal(1, p.Level(AbilityKind.LaunchTendency));
        Assert.Equal(0.0, p.Exp(AbilityKind.LaunchTendency));
    }

    [Fact]
    public void Up_AlreadyAtCeiling_DoesNothing()
    {
        var c = new TrainingCoefficients();
        var p = MakePlayer(99);
        DevelopmentModel.ApplyLaunchTendencyExp(p, 500.0, GrowthDirection.Up, c);
        Assert.Equal(99, p.Level(AbilityKind.LaunchTendency));
        Assert.Equal(0.0, p.Exp(AbilityKind.LaunchTendency));
    }

    [Fact]
    public void Down_AlreadyAtFloor_DoesNothing()
    {
        var c = new TrainingCoefficients();
        var p = MakePlayer(1);
        DevelopmentModel.ApplyLaunchTendencyExp(p, 500.0, GrowthDirection.Down, c);
        Assert.Equal(1, p.Level(AbilityKind.LaunchTendency));
        Assert.Equal(0.0, p.Exp(AbilityKind.LaunchTendency));
    }

    // --- 既知値: 下げ向きのexp計算（上げ向きと対称＝v→v-1の必要expで数える） ---

    [Fact]
    public void Down_KnownValue_UsesRequiredExpOfLowerLevel()
    {
        // trainability 既定0.5・GrowthMultiplier既定1.0（打撃分野）: scaled = 1500 × 1.0 × 0.5 = 750。
        // RequiredExp(49) ≈ 683.33（50→49に要るexp）。750 > 683.33 なので1段下がり、残exp ≈ 66.665。
        var c = new TrainingCoefficients();
        var p = MakePlayer(50);

        DevelopmentModel.ApplyLaunchTendencyExp(p, 1500.0, GrowthDirection.Down, c);

        Assert.Equal(49, p.Level(AbilityKind.LaunchTendency));
        Assert.Equal(66.665, p.Exp(AbilityKind.LaunchTendency), 2);
    }

    [Fact]
    public void Up_KnownValue_UsesRequiredExpOfCurrentLevel()
    {
        // 同じ scaled=750 を上げ向きに使うと RequiredExp(50) ≈ 710.67（50→51に要るexp）。
        // 750 > 710.67 なので1段上がり、残exp ≈ 39.33（下げ向きの残exp 66.665 とは非対称＝
        // 必要exp曲線が単調増加のため、低い方の必要expが小さいぶん下げが速く進む。これは意図どおり）。
        var c = new TrainingCoefficients();
        var p = MakePlayer(50);

        DevelopmentModel.ApplyLaunchTendencyExp(p, 1500.0, GrowthDirection.Up, c);

        Assert.Equal(51, p.Level(AbilityKind.LaunchTendency));
        Assert.Equal(39.33, p.Exp(AbilityKind.LaunchTendency), 2);
    }

    [Fact]
    public void Down_SmallDelta_AccumulatesExpWithoutLevelChange()
    {
        var c = new TrainingCoefficients();
        var p = MakePlayer(50);

        DevelopmentModel.ApplyLaunchTendencyExp(p, 10.0, GrowthDirection.Down, c);

        Assert.Equal(50, p.Level(AbilityKind.LaunchTendency));
        Assert.Equal(5.0, p.Exp(AbilityKind.LaunchTendency), 6); // scaled = 10 × 1.0 × 0.5 = 5
    }

    // --- trainability 係数が既定0.5（issue #1166: 1.0から絞った） ---

    [Fact]
    public void Trainability_LaunchTendency_DefaultIsNarrowedFromOne()
    {
        Assert.Equal(0.5, new TrainabilityCoefficients().LaunchTendency);
    }
}

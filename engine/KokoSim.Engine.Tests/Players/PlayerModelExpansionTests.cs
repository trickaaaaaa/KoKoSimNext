using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// 選手モデル拡張（2A, 設計書01 §1.1-1.2 / 02 §1.2・§4）の検証。
/// 走塁系連続パラメータ・肩/送球精度の分離・伸びしろ・守備適性・投手経歴の型と変換式。
/// </summary>
public sealed class PlayerModelExpansionTests
{
    // --- 送球精度 → 送球散布σ（設計書02 §1.2: 40 − Ac×0.36, 下限4cm） ---

    [Theory]
    [InlineData(50, 22.0)]   // 40 − 18 = 22
    [InlineData(100, 4.0)]   // 40 − 36 = 4
    [InlineData(1, 39.64)]   // 40 − 0.36
    public void ThrowAccuracy_MapsToScatterSigma(int accuracy, double expectedCm)
    {
        var f = new FielderAttributes { ThrowAccuracy = accuracy };
        Assert.Equal(expectedCm, f.ThrowScatterSigmaCm, 2);
    }

    [Fact]
    public void ThrowScatterSigma_NeverBelowFourCm()
    {
        var f = new FielderAttributes { ThrowAccuracy = 100 };
        Assert.True(f.ThrowScatterSigmaCm >= 4.0);
    }

    // --- 肩(ArmStrength)と送球精度は独立フィールド ---

    [Fact]
    public void ArmStrengthAndThrowAccuracy_AreIndependent()
    {
        var f = new FielderAttributes { ArmStrength = 90, ThrowAccuracy = 20 };
        Assert.Equal(90, f.ArmStrength);
        Assert.Equal(20, f.ThrowAccuracy);
        // 強肩でも精度が低ければσは大きい（悪送球）。
        Assert.True(f.ThrowScatterSigmaCm > 30.0);
    }

    // --- Player → FielderAttributes 投影が送球精度を伝える ---

    [Fact]
    public void Player_ToFielder_ProjectsThrowAccuracy()
    {
        var p = new Player { ArmStrength = 70, ThrowAccuracy = 35 };
        var f = p.ToFielder();
        Assert.Equal(70, f.ArmStrength);
        Assert.Equal(35, f.ThrowAccuracy);
    }

    // --- 走塁系連続パラメータが Player に載る ---

    [Fact]
    public void Player_HasRunningParameters()
    {
        var p = new Player { Bunt = 61, Steal = 72, Baserunning = 55 };
        Assert.Equal(61, p.Bunt);
        Assert.Equal(72, p.Steal);
        Assert.Equal(55, p.Baserunning);
    }

    // --- 投打の利き（既定は右） ---

    [Fact]
    public void Player_Handedness_DefaultsRight()
    {
        var p = new Player();
        Assert.Equal(Handedness.Right, p.Throws);
        Assert.Equal(Handedness.Right, p.Bats);
    }

    // --- 伸びしろ（分野別成長効率倍率）が経験値に乗る（設計書02 §5.1） ---

    [Fact]
    public void GrowthMultiplier_ScalesTrainingGain()
    {
        int GainWith(double battingGrowth)
        {
            var c = new TrainingCoefficients();
            var stages = new GrowthStageTable();
            var p = new DevelopingPlayer { GrowthType = GrowthType.Standard, BattingGrowth = battingGrowth };
            p.SetLevel(AbilityKind.Contact, 30);
            p.SetCap(AbilityKind.Contact, 99);
            for (var i = 0; i < 10; i++)
                DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 0, 1.0, stages, c);
            return p.Level(AbilityKind.Contact) - 30;
        }

        var normal = GainWith(1.0);
        var high = GainWith(2.0);
        Assert.True(high > normal, $"伸びしろ2.0({high}) > 1.0({normal})");
    }

    [Fact]
    public void GrowthMultiplier_DefaultsNeutral()
    {
        var p = new DevelopingPlayer();
        // 既定1.0 → 従来挙動と同一（分野別に既定値が入っている）。
        Assert.Equal(1.0, p.GrowthMultiplier(AbilityKind.Contact));
        Assert.Equal(1.0, p.GrowthMultiplier(AbilityKind.Velocity));
        Assert.Equal(1.0, p.GrowthMultiplier(AbilityKind.Fielding));
    }

    [Fact]
    public void GrowthMultiplier_RoutesByDomain()
    {
        var p = new DevelopingPlayer { PitchingGrowth = 3.0, BattingGrowth = 2.0, DefenseGrowth = 4.0 };
        Assert.Equal(3.0, p.GrowthMultiplier(AbilityKind.Control));      // 投手
        Assert.Equal(2.0, p.GrowthMultiplier(AbilityKind.Power));        // 打撃
        Assert.Equal(2.0, p.GrowthMultiplier(AbilityKind.Baserunning));  // 走塁→打撃分野
        Assert.Equal(4.0, p.GrowthMultiplier(AbilityKind.ThrowAccuracy));// 守備（送球精度）
        Assert.Equal(4.0, p.GrowthMultiplier(AbilityKind.ArmStrength));  // 守備（肩）
    }

    // --- 守備位置適性（設計書01 §1.1）: 9ポジション個別・既定50 ---

    [Fact]
    public void DevelopingPlayer_PositionAptitude_DefaultsFifty_AndIsSettable()
    {
        var p = new DevelopingPlayer();
        Assert.Equal(50, p.Aptitude(FieldPosition.Shortstop));
        p.SetAptitude(FieldPosition.Shortstop, 88);
        Assert.Equal(88, p.Aptitude(FieldPosition.Shortstop));
        Assert.Equal(50, p.Aptitude(FieldPosition.Pitcher)); // 他ポジは不変
    }

    // --- 投手経歴フラグ（設計書01 §1.1b） ---

    [Fact]
    public void PitcherBackground_FlagCarriesToPlayer()
    {
        var dp = new DevelopingPlayer { HasPitcherBackground = true };
        Assert.True(dp.HasPitcherBackground);
        var p = RosterTeamBuilder.ToPlayer(dp, FieldPosition.RightField, asPitcher: false);
        Assert.True(p.HasPitcherBackground);
    }

    // --- 守備位置適性→実効守備力（設計書01 §1.1） ---

    [Fact]
    public void PositionAptitude_ModulatesEffectiveFielding()
    {
        var dp = new DevelopingPlayer();
        dp.SetLevel(AbilityKind.Fielding, 70);
        dp.SetAptitude(FieldPosition.Shortstop, 90);   // 本職（高適性）
        dp.SetAptitude(FieldPosition.LeftField, 30);   // 慣れないポジ（低適性）

        var atSs = RosterTeamBuilder.ToPlayer(dp, FieldPosition.Shortstop, asPitcher: false);
        var atLf = RosterTeamBuilder.ToPlayer(dp, FieldPosition.LeftField, asPitcher: false);
        var atNeutral = RosterTeamBuilder.ToPlayer(dp, FieldPosition.CenterField, asPitcher: false); // 適性50=×1.0

        Assert.True(atSs.Fielding > atNeutral.Fielding);  // 高適性ポジは実効守備力↑
        Assert.True(atLf.Fielding < atNeutral.Fielding);  // 低適性ポジは↓
        Assert.Equal(70, atNeutral.Fielding);             // 基準適性50なら素の守備力そのまま
    }
}

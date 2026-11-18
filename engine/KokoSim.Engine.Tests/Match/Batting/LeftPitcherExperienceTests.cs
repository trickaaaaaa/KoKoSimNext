using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>対左投手経験（設計書02 §2.3「左のプレミア」・issue #1072）。§2.3bの試合内の慣れ（<see cref="FamiliarityTests"/>）とは
/// 別レイヤーの恒久寄りの個人差を検証する。</summary>
public sealed class LeftPitcherExperienceTests
{
    private static readonly BattingCoefficients C = new();
    private static readonly BatterAttributes Batter = new() { Contact = 50, Discipline = 50 };
    private static readonly PitchPlan Plan = new()
    {
        Type = PitchType.Fastball, AimX = 0, AimY = 0.6, VelocityKmh = 140, Stuff = 0.5, ControlLevel = 60,
    };
    private static PitchTrajectoryFeatures Feat(double breakM) => new(breakM, 0.0, 0.42);

    [Fact]
    public void Deficit_IsOneAtZeroPa_HalfAtHalfFullPa_ZeroAtFullPaAndBeyond()
    {
        Assert.Equal(1.0, ContactModel.LeftExperienceDeficit(0.0, C));
        Assert.Equal(0.5, ContactModel.LeftExperienceDeficit(C.LeftExperienceFullPa / 2.0, C), 9);
        Assert.Equal(0.0, ContactModel.LeftExperienceDeficit(C.LeftExperienceFullPa, C), 9);
        Assert.Equal(0.0, ContactModel.LeftExperienceDeficit(C.LeftExperienceFullPa * 2.0, C), 9);   // 上限で頭打ち
        // Player の既定値（安全な恒等セントネル）は常に0（経験十分扱い）。
        Assert.Equal(0.0, ContactModel.LeftExperienceDeficit(double.PositiveInfinity, C));
        Assert.Equal(double.PositiveInfinity, new Player().LeftExperiencePa);
    }

    [Fact]
    public void Whiff_IncreasesWithDeficit_AndIsUnaffectedAtZero()
    {
        var zero = ContactModel.WhiffProbability(Batter, Plan, Feat(0.1), true, C, leftExperienceDeficit: 0.0);
        var baseline = ContactModel.WhiffProbability(Batter, Plan, Feat(0.1), true, C);   // 既定引数（0.0）
        var half = ContactModel.WhiffProbability(Batter, Plan, Feat(0.1), true, C, leftExperienceDeficit: 0.5);
        var full = ContactModel.WhiffProbability(Batter, Plan, Feat(0.1), true, C, leftExperienceDeficit: 1.0);
        Assert.Equal(baseline, zero, 12);   // 既定引数を渡さない呼び出し＝deficit=0と1ビットも変わらない
        Assert.True(half > zero, $"経験不足0.5の空振り {half:F3} が0 {zero:F3} より増えていない");
        Assert.True(full > half, $"経験不足1.0の空振り {full:F3} が0.5 {half:F3} より増えていない");
    }

    [Fact]
    public void Chase_IncreasesWithDeficit_ButZoneSwingIsUnaffected()
    {
        var zero = BatterDecision.SwingProbability(false, 0.05, 0.1, Batter, C, leftExperienceDeficit: 0.0);
        var full = BatterDecision.SwingProbability(false, 0.05, 0.1, Batter, C, leftExperienceDeficit: 1.0);
        Assert.True(full > zero, $"経験不足1.0の追いかけ率 {full:F3} が0 {zero:F3} より増えていない");
        // ゾーン内のスイングは経験不足で変えない（§2.3bの慣れと同じ扱い）。
        Assert.Equal(
            BatterDecision.SwingProbability(true, 0, 0.1, Batter, C, leftExperienceDeficit: 0.0),
            BatterDecision.SwingProbability(true, 0, 0.1, Batter, C, leftExperienceDeficit: 1.0), 12);
    }

    [Fact]
    public void TeamState_CountsPaVsLeft_OnlyForLeftThrowingPitcher()
    {
        var team = DeterminismCards.Team("A");
        var state = new TeamState(team);
        var batter = team.BattingOrder[0];
        var lefty = new Player { Name = "L", Position = FieldPosition.Pitcher, Throws = Handedness.Left };
        var righty = new Player { Name = "R", Position = FieldPosition.Pitcher, Throws = Handedness.Right };

        state.RecordMatchup(batter, righty);
        Assert.Null(state.TodayBattingOf(batter));   // RecordBatting していない間は打席行自体が無い

        state.RecordMatchup(batter, lefty);
        state.RecordMatchup(batter, lefty);
        state.RecordBatting(batter, PlateAppearanceResult.Single, 0);
        var line = state.TodayBattingOf(batter);
        Assert.NotNull(line);
        Assert.Equal(2, line!.PlateAppearancesVsLeft);   // 右投手ぶんは数えない
    }

    private static DevelopingPlayer Dp(int id) => new() { Id = id, Name = "選手" + id };

    [Fact]
    public void GrowthModel_AccumulatesWeightedPa_OfficialHeavierThanPractice()
    {
        var dpOfficial = Dp(1);
        var dpPractice = Dp(2);
        static GameResult DetailFor(DevelopingPlayer dp) => new()
        {
            AwayName = "Away", HomeName = "Home", AwayRuns = 0, HomeRuns = 0,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            AwayBatting = new[]
            {
                new BattingLine(1, FieldPosition.CenterField, dp.Name, 4, 4, 1, 0, 0, 0, 0, 0, 0, dp.Id)
                    { PlateAppearancesVsLeft = 4 },
            },
            HomeBatting = System.Array.Empty<BattingLine>(),
        };

        LeftExperienceGrowthModel.Apply(DetailFor(dpOfficial), managerIsAway: true, new[] { dpOfficial }, isOfficial: true, new BattingCoefficients());
        LeftExperienceGrowthModel.Apply(DetailFor(dpPractice), managerIsAway: true, new[] { dpPractice }, isOfficial: false, new BattingCoefficients());

        var c = new BattingCoefficients();
        Assert.Equal(4 * c.LeftExperienceOfficialPaWeight, dpOfficial.LeftExperiencePa, 9);
        Assert.Equal(4 * c.LeftExperiencePracticePaWeight, dpPractice.LeftExperiencePa, 9);
        Assert.True(dpOfficial.LeftExperiencePa > dpPractice.LeftExperiencePa);
    }

    [Fact]
    public void GrowthModel_IgnoresUnknownSourceId_AndZeroPaVsLeft()
    {
        var dp = Dp(1);
        var detail = new GameResult
        {
            AwayName = "Away", HomeName = "Home", AwayRuns = 0, HomeRuns = 0,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            AwayBatting = new[]
            {
                new BattingLine(1, FieldPosition.CenterField, "無関係", 4, 4, 1, 0, 0, 0, 0, 0, 0, 999) { PlateAppearancesVsLeft = 4 },
                new BattingLine(2, FieldPosition.Shortstop, dp.Name, 4, 4, 1, 0, 0, 0, 0, 0, 0, dp.Id) { PlateAppearancesVsLeft = 0 },
            },
            HomeBatting = System.Array.Empty<BattingLine>(),
        };
        LeftExperienceGrowthModel.Apply(detail, managerIsAway: true, new[] { dp }, isOfficial: true, new BattingCoefficients());
        Assert.Equal(0.0, dp.LeftExperiencePa);
    }
}

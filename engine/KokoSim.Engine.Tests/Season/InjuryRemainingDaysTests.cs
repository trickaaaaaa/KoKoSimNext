using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 大会中（日送り）の完治までの残り日数（issue #870）。
/// 日送りの規則（<c>TournamentDay % 7 == 0</c> の日に怪我の週次処理）を実際に回して表示値と突き合わせる。
/// </summary>
public sealed class InjuryRemainingDaysTests
{
    private static readonly InjuryCoefficients Coeff = new();

    private static DevelopingPlayer Injured(InjurySeverity severity, InjuryType type, int weeksRemaining)
        => new DevelopingPlayer
        {
            Injury = severity,
            InjuryType = type,
            InjurySite = InjurySite.Ankle,
            InjuryWeeksRemaining = weeksRemaining,
        };

    /// <summary>日送りを1日ずつ回し、完治する日までの日数を数える（GameSession.AdvanceDay と同じ周期）。</summary>
    private static int SimulateDaysToHeal(DevelopingPlayer p, int startDay)
    {
        var day = startDay;
        var days = 0;
        while (p.Injury != InjurySeverity.None)
        {
            day++;
            days++;
            if (day % 7 == 0) InjuryModel.WeeklyRecover(p, Coeff);
            Assert.InRange(days, 0, 1000);
        }
        return days;
    }

    [Theory]
    [InlineData(InjurySeverity.Minor, 0)]
    [InlineData(InjurySeverity.Minor, 3)]
    [InlineData(InjurySeverity.Moderate, 0)]
    [InlineData(InjurySeverity.Moderate, 6)]
    [InlineData(InjurySeverity.Severe, 0)]
    [InlineData(InjurySeverity.Severe, 4)]
    [InlineData(InjurySeverity.Severe, 13)]
    public void DaysToFullRecovery_MatchesActualDaySteppedRecovery(InjurySeverity severity, int startDay)
    {
        foreach (var type in new[] { InjuryType.None, InjuryType.Sprain, InjuryType.Fracture })
        {
            var weeks0 = InjuryModel.RecoveryWeeks(severity, Coeff, 1.0);
            var p = Injured(severity, type, weeks0);
            var predicted = InjuryModel.DaysToFullRecovery(InjuryModel.WeeksToFullRecovery(p, Coeff), startDay);
            Assert.Equal(SimulateDaysToHeal(p, startDay), predicted);
        }
    }

    [Fact]
    public void DaysToFullRecovery_DropsByOneWeekOnEachTickDay()
    {
        // 回復処理の日（7の倍数）を跨ぐたびに、残りは週数が1つ減って 7日ぶん小さくなる。
        Assert.Equal(7, InjuryModel.DaysToFullRecovery(1, 0));
        Assert.Equal(1, InjuryModel.DaysToFullRecovery(1, 6));
        Assert.Equal(7, InjuryModel.DaysToFullRecovery(1, 7));
        Assert.Equal(14, InjuryModel.DaysToFullRecovery(2, 7));
        Assert.Equal(8, InjuryModel.DaysToFullRecovery(2, 6));
        Assert.Equal(1, InjuryModel.DaysToFullRecovery(2, 13) - 7);
    }

    [Fact]
    public void DaysToFullRecovery_Healthy_IsZero()
        => Assert.Equal(0, InjuryModel.DaysToFullRecovery(0, 3));

    [Fact]
    public void WeeksToFullRecovery_SevereAddsLowerStageWeeks()
    {
        var severe = Injured(InjurySeverity.Severe, InjuryType.None, 2);
        var moderate = Injured(InjurySeverity.Moderate, InjuryType.None, 2);
        var minor = Injured(InjurySeverity.Minor, InjuryType.None, 2);

        var wMinor = InjuryModel.WeeksToFullRecovery(minor, Coeff);
        var wModerate = InjuryModel.WeeksToFullRecovery(moderate, Coeff);
        var wSevere = InjuryModel.WeeksToFullRecovery(severe, Coeff);

        Assert.Equal(2, wMinor);
        Assert.Equal(2 + InjuryModel.RecoveryWeeks(InjurySeverity.Minor, Coeff, 1.0), wModerate);
        Assert.Equal(wModerate + InjuryModel.RecoveryWeeks(InjurySeverity.Moderate, Coeff, 1.0), wSevere);
        Assert.Equal(0, InjuryModel.WeeksToFullRecovery(new DevelopingPlayer(), Coeff));
    }
}

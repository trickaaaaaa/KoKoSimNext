using System.Linq;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>個別指導の指名・解除（issue #707）。</summary>
public class IndividualCoachingNominationTests
{
    private static DevelopingPlayer[] Roster(int n)
        => Enumerable.Range(0, n).Select(_ => new DevelopingPlayer()).ToArray();

    [Fact]
    public void Toggle_NominatesUntilSlotsAreFull_ThenRefuses()
    {
        var roster = Roster(5);

        Assert.Equal(NominationResult.Nominated, IndividualCoachingNomination.Toggle(roster, 0, 3));
        Assert.Equal(NominationResult.Nominated, IndividualCoachingNomination.Toggle(roster, 1, 3));
        Assert.Equal(NominationResult.Nominated, IndividualCoachingNomination.Toggle(roster, 2, 3));
        Assert.Equal(NominationResult.Full, IndividualCoachingNomination.Toggle(roster, 3, 3));

        Assert.False(roster[3].IndividualCoaching);
        Assert.Equal(3, IndividualCoachingNomination.CountNominated(roster));
    }

    [Fact]
    public void Toggle_ReleasesNominatedPlayer_EvenWhenFull()
    {
        var roster = Roster(4);
        for (var i = 0; i < 3; i++) IndividualCoachingNomination.Toggle(roster, i, 3);

        Assert.Equal(NominationResult.Released, IndividualCoachingNomination.Toggle(roster, 1, 3));
        Assert.False(roster[1].IndividualCoaching);

        // 空いた枠には別の選手を指名できる
        Assert.Equal(NominationResult.Nominated, IndividualCoachingNomination.Toggle(roster, 3, 3));
        Assert.Equal(3, IndividualCoachingNomination.CountNominated(roster));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Toggle_OutOfRange_ChangesNothing(int index)
    {
        var roster = Roster(3);
        Assert.Equal(NominationResult.Invalid, IndividualCoachingNomination.Toggle(roster, index, 3));
        Assert.Equal(0, IndividualCoachingNomination.CountNominated(roster));
    }
}

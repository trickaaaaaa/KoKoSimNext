using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>大会中は背番号・ベンチ入りを固める（自校が残っている間だけ。負け・優勝のあと・大会の外では変えられる）。</summary>
public sealed class UniformNumberLockTests
{
    private static School Sch(int id, double strength) => new() { Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength };

    private static TournamentRunner NewRunner(double managerStrength, double othersStrength)
    {
        var field = new List<School> { Sch(1, managerStrength) };
        for (var i = 0; i < 7; i++) field.Add(Sch(100 + i, othersStrength));
        return new TournamentRunner(field, field[0], new NationCoefficients(), new Xoshiro256Random(42),
            new TournamentSchedule(), "テスト大会");
    }

    [Fact]
    public void NotInTournament_IsNotLocked() => Assert.False(UniformNumberLock.IsLocked(null));

    [Fact]
    public void WhileManagerSurvives_IsLocked_UntilChampion()
    {
        var r = NewRunner(99, 30);
        while (!r.Finished)
        {
            Assert.True(UniformNumberLock.IsLocked(r));
            r.PlayNextPlayerMatch();
        }
        Assert.True(r.IsChampion);
        Assert.False(UniformNumberLock.IsLocked(r));
    }

    [Fact]
    public void AfterManagerLoses_IsNotLocked_EvenWhileRestOfBracketRemains()
    {
        var r = NewRunner(1, 99);
        Assert.True(UniformNumberLock.IsLocked(r));
        while (!r.Finished) r.PlayNextPlayerMatch();
        Assert.False(r.IsChampion);
        Assert.False(UniformNumberLock.IsLocked(r));
    }
}

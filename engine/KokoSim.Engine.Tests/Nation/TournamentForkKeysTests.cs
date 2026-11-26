using System.Linq;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

public class TournamentForkKeysTests
{
    [Fact]
    public void SameScopeKeys_CannotProduceTheSameStreamId()
    {
        var all = TournamentForkKeys.All;
        var collisions = (
            from i in Enumerable.Range(0, all.Count)
            from j in Enumerable.Range(i + 1, all.Count - i - 1)
            where TournamentForkKeys.CanCollide(all[i], all[j])
            select $"{all[i].Name} <-> {all[j].Name}").ToList();
        Assert.Empty(collisions);
    }

    [Fact]
    public void KeyNames_AreUnique()
        => Assert.Equal(TournamentForkKeys.All.Count, TournamentForkKeys.All.Select(k => k.Name).Distinct().Count());

    [Fact]
    public void CanCollide_DetectsOverlappingVariableBits()
    {
        var a = new ForkKey("a", 0xB10C_0000UL, 0xFFFF_FFFFUL, ForkScope.Tournament);
        var b = new ForkKey("b", 0xB10C_D2A3UL, 0, ForkScope.Tournament);
        Assert.True(TournamentForkKeys.CanCollide(a, b));
        Assert.False(TournamentForkKeys.CanCollide(a, b with { Scope = ForkScope.SummerRoot }));
    }

    [Fact]
    public void CardStream_AttemptZeroMatchesRoundSlotOnlyId_AndAttemptsDiffer()
    {
        var key = TournamentForkKeys.KnockoutBackground;
        Assert.Equal(key ^ (3UL << 16) ^ 5UL, TournamentForkKeys.CardStream(key, 3, 5));
        Assert.NotEqual(TournamentForkKeys.CardStream(key, 3, 5), TournamentForkKeys.CardStream(key, 3, 5, attempt: 1));
    }

    [Fact]
    public void CardStreams_AreDistinctAcrossRoundsSlotsAndAttempts_ForEveryCardKey()
    {
        foreach (var key in new[]
        {
            TournamentForkKeys.KnockoutBackground, TournamentForkKeys.KnockoutPlayer,
            TournamentForkKeys.LeagueBackground, TournamentForkKeys.LeaguePlayer,
        })
        {
            var ids = from round in Enumerable.Range(0, 8)
                      from slot in Enumerable.Range(0, 64)
                      from attempt in Enumerable.Range(0, 3)
                      select TournamentForkKeys.CardStream(key, round, slot, attempt);
            var list = ids.ToList();
            Assert.Equal(list.Count, list.Distinct().Count());
        }
    }

    [Fact]
    public void CardKeys_NeverOverlapEachOtherAcrossRealisticInputs()
    {
        var keys = new[]
        {
            TournamentForkKeys.KnockoutBackground, TournamentForkKeys.KnockoutPlayer,
            TournamentForkKeys.LeagueBackground, TournamentForkKeys.LeaguePlayer,
        };
        var ids = keys.SelectMany(k =>
            from round in Enumerable.Range(0, 8)
            from slot in Enumerable.Range(0, 64)
            from attempt in Enumerable.Range(0, 3)
            select TournamentForkKeys.CardStream(k, round, slot, attempt)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void PrefectureStreams_AreDistinctPerPrefectureAndSplit()
    {
        var summer = (from p in Enumerable.Range(0, 60)
                      from split in new int?[] { null, 0, 1 }
                      select TournamentForkKeys.SummerPrefectureStream(p, split)).ToList();
        Assert.Equal(summer.Count, summer.Distinct().Count());
        Assert.Equal(TournamentForkKeys.SummerPrefecture ^ 7UL, TournamentForkKeys.SummerPrefectureStream(7, null));
        var autumn = Enumerable.Range(0, 60).Select(TournamentForkKeys.AutumnPrefectureStream).ToList();
        Assert.Equal(autumn.Count, autumn.Distinct().Count());
    }
}

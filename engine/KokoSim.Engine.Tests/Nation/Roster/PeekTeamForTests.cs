using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using Xunit;

namespace KokoSim.Engine.Tests.Nation.Roster;

/// <summary>
/// 表示用の「保存せずに覗く」チーム（issue #737）。いま戦えば出てくるチームと同じ総合になり、
/// 覗いただけではロスターが生成・保存されない（画面を開いたかで世界が変わらない）。
/// </summary>
public sealed class PeekTeamForTests
{
    private static readonly TeamStrengthCoefficients C = new();
    private static School S(int id, double strength)
        => new() { Id = id, Name = "校" + id, PrefectureId = 14, Strength = strength };

    [Fact]
    public void Peek_DoesNotStoreRoster_AndMatchesTheTeamYouWouldFace()
    {
        var rosters = new NationRosters(new AiRosterDeps());
        var school = S(501, 63);

        var peeked = ScoutedTeamProfile.Compute(rosters.PeekTeamFor(school, 1), C);
        Assert.Empty(rosters.Schools);   // 覗いただけでは保存しない

        var faced = ScoutedTeamProfile.Compute(rosters.TeamFor(school, 1), C);
        Assert.Equal(faced, peeked);
        Assert.Contains(501, rosters.Schools);
    }

    [Fact]
    public void Peek_UsesStoredRoster_WhenAlreadyBootstrapped()
    {
        var rosters = new NationRosters(new AiRosterDeps());
        var school = S(502, 55);
        var faced = ScoutedTeamProfile.Compute(rosters.TeamFor(school, 1), C);
        // 以後は保存済みのロスター（年度が違っても再生成しない）から組む。
        Assert.Equal(faced, ScoutedTeamProfile.Compute(rosters.PeekTeamFor(school, 1), C));
        Assert.Single(rosters.Schools);
    }
}

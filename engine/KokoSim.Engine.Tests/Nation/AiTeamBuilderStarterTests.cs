using System.Linq;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>AiTeamBuilder の先発選択（エース温存, issue #42）の検証。</summary>
public sealed class AiTeamBuilderStarterTests
{
    private static School MakeSchool(int id, double strength) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 13, Strength = strength,
    };

    private static AceRestContext ForcedRest => new(Tier.G, RoundsRemaining: 3, MatchDay: 10, Ledger: null);

    private static readonly EnemyAiCoefficients AlwaysRest = new() { AceRestFloor = 1.0, AceRestCap = 1.0 };

    [Fact]
    public void NoAceRestContext_AlwaysStartsRankedAce()
    {
        var deps = new AiRosterDeps();
        var school = MakeSchool(1, 90);
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);

        var team = AiTeamBuilder.Build(roster, school, 1, deps);

        var starter = team.BattingOrder[team.PitcherSlot];
        Assert.Equal(AceRestSelector.AceUniformNumber, starter.UniformNumber);
        Assert.DoesNotContain(team.Bullpen, p => p.UniformNumber == AceRestSelector.AceUniformNumber);
    }

    [Fact]
    public void AceRestForced_BenchesAce_StartsNextPitcher()
    {
        var deps = new AiRosterDeps { EnemyAi = AlwaysRest };
        var school = MakeSchool(1, 90);   // S級＝ AceRestMinTier(既定3)のゲートを通る
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);

        var team = AiTeamBuilder.Build(roster, school, 1, deps, aceRest: ForcedRest);

        var starter = team.BattingOrder[team.PitcherSlot];
        Assert.NotEqual(AceRestSelector.AceUniformNumber, starter.UniformNumber);
        Assert.Contains(team.Bullpen, p => p.UniformNumber == AceRestSelector.AceUniformNumber);
    }

    [Fact]
    public void AceIdentity_KeepsSameJersey_WhetherStartingOrBenched()
    {
        var baseDeps = new AiRosterDeps();
        var school = MakeSchool(1, 90);
        var roster = AiRosterFactory.Bootstrap(school, 1, baseDeps);

        var teamAceStarts = AiTeamBuilder.Build(roster, school, 1, baseDeps);
        var aceWhenStarting = teamAceStarts.BattingOrder[teamAceStarts.PitcherSlot];

        var forcedDeps = baseDeps with { EnemyAi = AlwaysRest };
        var teamAceRests = AiTeamBuilder.Build(roster, school, 1, forcedDeps, aceRest: ForcedRest);
        var aceWhenBenched = teamAceRests.Bullpen.First(p => p.UniformNumber == AceRestSelector.AceUniformNumber);

        // 温存の有無に関わらず、真のエースの背番号(1)とSourceId(同一個人)は不変＝台帳キー同定の安定性。
        Assert.Equal(aceWhenStarting.SourceId, aceWhenBenched.SourceId);
    }

    [Fact]
    public void BelowAceRestMinTier_IgnoresContext_StillStartsAce()
    {
        var deps = new AiRosterDeps
        {
            EnemyAi = AlwaysRest with { AceRestMinTier = 3 },
        };
        var school = MakeSchool(1, 45);   // Tier E(2) < AceRestMinTier(3)
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);

        var team = AiTeamBuilder.Build(roster, school, 1, deps, aceRest: ForcedRest);

        var starter = team.BattingOrder[team.PitcherSlot];
        Assert.Equal(AceRestSelector.AceUniformNumber, starter.UniformNumber);
    }

    /// <summary>issue #191: 永続ロスター（3学年）は20人を大きく超えるため、打ち切りが無いと背番号21以降が
    /// ベンチ/ブルペンに混入する。ベンチ入り20人制（背番号1〜20）に収まることを検証する。</summary>
    [Fact]
    public void Build_CapsRosterAtTwentyUniformNumbers()
    {
        var deps = new AiRosterDeps();
        var school = MakeSchool(1, 70);
        var roster = AiRosterFactory.Bootstrap(school, 1, deps);   // 3学年ぶん＝20人を大きく超える母数

        var team = AiTeamBuilder.Build(roster, school, 1, deps);

        var all = team.BattingOrder.Concat(team.Bench).Concat(team.Bullpen).ToList();
        Assert.All(all, p => Assert.InRange(p.UniformNumber, 1, 20));
        Assert.Equal(all.Count, all.Select(p => p.UniformNumber).Distinct().Count());
        Assert.True(all.Count <= 20);
    }
}

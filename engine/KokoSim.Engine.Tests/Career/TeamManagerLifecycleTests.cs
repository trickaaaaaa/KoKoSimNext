using KokoSim.Engine.Core;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// マネージャー（NPC）の入れ替わり（design-04 §1.3a・issue #1430）。
/// 新設校＝1年／転任＝2か3年／3年夏で引退して1年が付く／見た目が直前と同じにならない／
/// 同じシードで同じ並び（不変条件#2）を担保する。
/// </summary>
public sealed class TeamManagerLifecycleTests
{
    [Fact]
    public void AssignForNewSchool_SetsGradeOne_AndPortraitInRange()
    {
        var m = new KokoSim.Engine.Career.Manager();

        KokoSim.Engine.Career.TeamManagerLifecycle.AssignForNewSchool(m, new Xoshiro256Random(1));

        Assert.Equal(1, m.TeamManagerGrade);
        Assert.InRange(m.TeamManagerPortrait, 1, KokoSim.Engine.Career.TeamManagerLifecycle.PortraitCount);
    }

    [Fact]
    public void AssignForNewSchool_NeverPicksGradeTwoOrThree_AcrossManySeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var m = new KokoSim.Engine.Career.Manager();
            KokoSim.Engine.Career.TeamManagerLifecycle.AssignForNewSchool(m, new Xoshiro256Random(seed));
            Assert.Equal(1, m.TeamManagerGrade);
        }
    }

    [Fact]
    public void AssignForTransfer_SetsGradeTwoOrThree_AcrossManySeeds()
    {
        var sawTwo = false;
        var sawThree = false;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var m = new KokoSim.Engine.Career.Manager { TeamManagerPortrait = 1 };
            KokoSim.Engine.Career.TeamManagerLifecycle.AssignForTransfer(m, new Xoshiro256Random(seed));
            Assert.True(m.TeamManagerGrade == 2 || m.TeamManagerGrade == 3,
                $"転任は2年か3年のはず（実際: {m.TeamManagerGrade}）");
            sawTwo |= m.TeamManagerGrade == 2;
            sawThree |= m.TeamManagerGrade == 3;
        }
        Assert.True(sawTwo, "200シード回して2年が一度も出ない");
        Assert.True(sawThree, "200シード回して3年が一度も出ない");
    }

    [Fact]
    public void AssignForTransfer_PortraitNeverRepeatsThePrevious_AcrossManySeeds()
    {
        for (var previous = 1; previous <= KokoSim.Engine.Career.TeamManagerLifecycle.PortraitCount; previous++)
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var m = new KokoSim.Engine.Career.Manager { TeamManagerPortrait = previous };
            KokoSim.Engine.Career.TeamManagerLifecycle.AssignForTransfer(m, new Xoshiro256Random(seed));
            Assert.NotEqual(previous, m.TeamManagerPortrait);
            Assert.InRange(m.TeamManagerPortrait, 1, KokoSim.Engine.Career.TeamManagerLifecycle.PortraitCount);
        }
    }

    [Fact]
    public void IsRetiring_TrueOnlyAtGradeThreeOrMore()
    {
        Assert.False(KokoSim.Engine.Career.TeamManagerLifecycle.IsRetiring(new KokoSim.Engine.Career.Manager { TeamManagerGrade = 1 }));
        Assert.False(KokoSim.Engine.Career.TeamManagerLifecycle.IsRetiring(new KokoSim.Engine.Career.Manager { TeamManagerGrade = 2 }));
        Assert.True(KokoSim.Engine.Career.TeamManagerLifecycle.IsRetiring(new KokoSim.Engine.Career.Manager { TeamManagerGrade = 3 }));
    }

    [Fact]
    public void Succeed_ResetsToGradeOne_CapturesOutgoingPortrait_AndNeverRepeatsIt()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var m = new KokoSim.Engine.Career.Manager { TeamManagerGrade = 3, TeamManagerPortrait = 7 };

            KokoSim.Engine.Career.TeamManagerLifecycle.Succeed(m, new Xoshiro256Random(seed));

            Assert.Equal(1, m.TeamManagerGrade);
            Assert.Equal(7, m.OutgoingTeamManagerPortrait);
            Assert.NotEqual(7, m.TeamManagerPortrait);
            Assert.InRange(m.TeamManagerPortrait, 1, KokoSim.Engine.Career.TeamManagerLifecycle.PortraitCount);
        }
    }

    [Fact]
    public void AdvanceGrade_Increments_ButStopsAtThree()
    {
        var m = new KokoSim.Engine.Career.Manager { TeamManagerGrade = 1 };

        KokoSim.Engine.Career.TeamManagerLifecycle.AdvanceGrade(m);
        Assert.Equal(2, m.TeamManagerGrade);

        KokoSim.Engine.Career.TeamManagerLifecycle.AdvanceGrade(m);
        Assert.Equal(3, m.TeamManagerGrade);

        KokoSim.Engine.Career.TeamManagerLifecycle.AdvanceGrade(m);
        Assert.Equal(3, m.TeamManagerGrade);
    }

    [Fact]
    public void SameSeed_ProducesTheSameAssignment_EveryTime()
    {
        var a = new KokoSim.Engine.Career.Manager { TeamManagerPortrait = 3 };
        var b = new KokoSim.Engine.Career.Manager { TeamManagerPortrait = 3 };

        KokoSim.Engine.Career.TeamManagerLifecycle.AssignForTransfer(a, new Xoshiro256Random(0xABCDUL));
        KokoSim.Engine.Career.TeamManagerLifecycle.AssignForTransfer(b, new Xoshiro256Random(0xABCDUL));

        Assert.Equal(a.TeamManagerGrade, b.TeamManagerGrade);
        Assert.Equal(a.TeamManagerPortrait, b.TeamManagerPortrait);
    }
}

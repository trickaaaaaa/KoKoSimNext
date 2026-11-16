using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation.Roster;

/// <summary>
/// issue #1047: 監督傾向のうち「チームを組む時点で付ける」3つ（エース酷使・継投早め・抜擢型）が、
/// 実際の試合の相手校＝永続ロスター（<see cref="NationRosters.TeamFor"/> → <see cref="AiTeamBuilder"/>）でも効く。
/// 旧実装は使い捨て生成（StrengthTeamFactory）にしか処理が無く、#80 以降の実ゲームでは3つとも効いていなかった。
/// </summary>
public sealed class ManagerTraitPersistentRosterTests
{
    private static readonly EnemyAiCoefficients Ai = new();

    private static School Sch(int id, double strength, params ManagerTrait[] traits) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 14, Strength = strength, ManagerTraits = traits,
    };

    [Fact]
    public void AceOveruse_And_QuickHook_SetRelieveMargin_OnPersistentRosterTeam()
    {
        var rosters = new NationRosters(new AiRosterDeps());
        Assert.Equal(1.0, rosters.TeamFor(Sch(601, 60), 1).RelieveMarginFactor);
        Assert.Equal(Ai.AceOveruseRelieveMarginFactor,
            rosters.TeamFor(Sch(602, 60, ManagerTrait.AceOveruse), 1).RelieveMarginFactor, 9);
        Assert.Equal(Ai.QuickHookRelieveMarginFactor,
            rosters.TeamFor(Sch(603, 60, ManagerTrait.QuickHook), 1).RelieveMarginFactor, 9);
        // 表示用の覗き見（PeekTeamFor）も同じ組み方を通る。
        Assert.Equal(Ai.QuickHookRelieveMarginFactor,
            rosters.PeekTeamFor(Sch(604, 60, ManagerTrait.QuickHook), 1).RelieveMarginFactor, 9);
    }

    /// <summary>
    /// 抜擢型: 同じ学校（同じロスター・同じ当季の調子）で、傾向なしの先発から外れていた好調な選手が先発に入る年がある。
    /// 新しく先発に入る選手は必ず好調（Good 以上）＝不調・普通の控えを抜擢しない。
    /// </summary>
    [Fact]
    public void Promoter_PutsInFormBenchPlayerIntoLineup_OnPersistentRosterTeam()
    {
        var promotedYears = 0;
        for (var id = 700; id < 760; id++)
        {
            // 同じ Id ＝同じロスター・同じ調子。傾向の有無だけが違う。
            var plain = new NationRosters(new AiRosterDeps()).TeamFor(Sch(id, 62), 1);
            var pro = new NationRosters(new AiRosterDeps()).TeamFor(Sch(id, 62, ManagerTrait.Promoter), 1);

            var plainIds = plain.BattingOrder.Select(p => p.SourceId).ToHashSet();
            var newcomers = pro.BattingOrder.Where(p => !plainIds.Contains(p.SourceId)).ToList();
            if (newcomers.Count == 0) continue;
            promotedYears++;
            Assert.All(newcomers, p =>
                Assert.True(FormModel.Step(p.Condition) >= Ai.PromoterMinConditionStep,
                    $"抜擢されるのは好調な選手だけのはず: 校{id} {p.Name} {p.Condition}"));
        }
        Assert.True(promotedYears > 0, "60校のうち1校も抜擢が起きていない＝抜擢型が効いていない");
    }

    [Fact]
    public void Promoter_IsDeterministic_PeekEqualsFaced()
    {
        var school = Sch(777, 62, ManagerTrait.Promoter);
        var peeked = new NationRosters(new AiRosterDeps()).PeekTeamFor(school, 1);
        var faced = new NationRosters(new AiRosterDeps()).TeamFor(school, 1);
        Assert.Equal(peeked.BattingOrder.Select(p => p.SourceId), faced.BattingOrder.Select(p => p.SourceId));
    }

    /// <summary>試合を通しての観測: 永続ロスターでも、エース酷使は継投早めより先発を引っ張る。</summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void AceOveruse_ThrowsStarterLongerThanQuickHook_OnPersistentRoster()
    {
        var opp = new NationRosters(new AiRosterDeps()).TeamFor(Sch(901, 60), 1);
        (int starterPitches, int changes) Run(ManagerTrait trait)
        {
            // 同じ Id ＝同じロスター。差は継投の余裕の倍率だけ。
            var team = new NationRosters(new AiRosterDeps()).TeamFor(Sch(900, 70, trait), 1);
            var results = new GameResult[60];
            System.Threading.Tasks.Parallel.For(0, results.Length, i =>
                results[i] = GameEngine.Play(team, opp, new GameContext(), new Xoshiro256Random(27000UL + (ulong)i)));
            return (results.Sum(r => r.AwayPitching.Count > 0 ? r.AwayPitching[0].Pitches : 0),
                    results.Sum(r => r.AwayPitching.Count - 1));
        }

        var (overuseP, overuseC) = Run(ManagerTrait.AceOveruse);
        var (quickP, quickC) = Run(ManagerTrait.QuickHook);
        Assert.True(overuseP > quickP, $"エース酷使は先発の球数が多いはず: overuse={overuseP} quick={quickP}");
        Assert.True(overuseC < quickC, $"継投早めの方が継投が多いはず: overuse={overuseC} quick={quickC}");
    }
}

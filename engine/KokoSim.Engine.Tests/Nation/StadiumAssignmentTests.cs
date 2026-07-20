using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会→球場の割当（設計書13-stadiums §2-2）。球場は試合レコードのメタ情報で、勝敗（AggregateMatch）には効かない。
/// 記録の有無で大会結果が変わらないこと（決定論・不変条件#2）も保証する。
/// </summary>
public sealed class StadiumAssignmentTests
{
    private static readonly NationCoefficients Coeff = new();

    private static School Sch(int id) => new() { Id = id, Name = $"校{id}", PrefectureId = 0, Strength = 20 + (id * 61 % 70) };

    private static System.Collections.Generic.List<School> Schools(int n) => Enumerable.Range(0, n).Select(Sch).ToList();

    private static PrefFormat KnockoutFormat(StadiumPlan plan) => new()
    {
        Pref = "test",
        Stages = new[] { new StageFormat { Name = "県大会", Type = StageType.Knockout } },
        Stadiums = plan,
    };

    [Fact]
    public void StadiumPlan_Assign_FinalRoundsUseFinal_EarlyUsePool()
    {
        var plan = new StadiumPlan("kanagawa_final", new[] { "municipal_small_01", "municipal_small_02" });
        var rng = new Xoshiro256Random(1);

        Assert.Equal("kanagawa_final", plan.Assign(1, rng)); // 決勝
        Assert.Equal("kanagawa_final", plan.Assign(2, rng)); // 準決勝
        for (var i = 0; i < 10; i++)
        {
            var s = plan.Assign(3, rng); // 序盤
            Assert.Contains(s, new[] { "municipal_small_01", "municipal_small_02" });
        }
    }

    [Fact]
    public void Run_RecordMatches_ProducesRecordsWithStadiums_FinalIsFinalStadium()
    {
        var plan = new StadiumPlan("kanagawa_final", new[] { "municipal_small_01", "municipal_small_02" });
        var r = PrefTournamentEngine.Run(KnockoutFormat(plan), Schools(16), Coeff, new Xoshiro256Random(5),
            recordMatches: true);

        Assert.NotEmpty(r.Matches);
        Assert.Equal(15, r.Matches.Count); // 16校ノックアウト＝15試合

        // 決勝（残り1ラウンド）は横浜、序盤は市営プール
        var final = r.Matches.Single(m => m.RoundsRemaining == 1);
        Assert.Equal("kanagawa_final", final.StadiumId);
        Assert.Equal(r.Champion, final.Winner);

        var early = r.Matches.Where(m => m.RoundsRemaining >= 3);
        Assert.All(early, m => Assert.Contains(m.StadiumId, new[] { "municipal_small_01", "municipal_small_02" }));
    }

    [Fact]
    public void Recording_DoesNotChangeOutcome_Deterministic()
    {
        var fmt = KnockoutFormat(new StadiumPlan("kanagawa_final", new[] { "municipal_small_01" }));
        var teams = Schools(24);

        var plain = PrefTournamentEngine.Run(fmt, teams, Coeff, new Xoshiro256Random(9));
        var recorded = PrefTournamentEngine.Run(fmt, teams, Coeff, new Xoshiro256Random(9), recordMatches: true);

        // 球場抽選は fork した専用ストリームなので、本編の勝敗は記録の有無で一切変わらない。
        Assert.Equal(plain.Champion, recorded.Champion);
        Assert.Equal(plain.FinalPlacement, recorded.FinalPlacement);
        Assert.Empty(plain.Matches);
        Assert.NotEmpty(recorded.Matches);
    }

    [Fact]
    public void NoStadiumPlan_RecordsHaveEmptyStadiumId()
    {
        var r = PrefTournamentEngine.Run(KnockoutFormat(StadiumPlan.None), Schools(8), Coeff,
            new Xoshiro256Random(3), recordMatches: true);
        Assert.All(r.Matches, m => Assert.Equal("", m.StadiumId));
    }

    [Fact]
    public void PrefFormatLoader_ParsesStadiumsBlock()
    {
        var fmt = PrefFormatLoader.LoadFromFile(Balance.BalanceRegressionTests.FindDataFile("pref-formats/kanagawa.yaml"));
        Assert.Equal("kanagawa_final", fmt.Stadiums.FinalId);
        Assert.Contains("municipal_small_01", fmt.Stadiums.EarlyIds);
        Assert.False(fmt.Stadiums.IsEmpty);
    }
}

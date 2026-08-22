using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// <see cref="TournamentRunner"/> の決着済みブラケットから順位を逆算する
/// <see cref="KnockoutPlacement.FromMatches"/>（設計書05 §1.5b・地区大会の出場枠判定に使う）。
/// </summary>
public sealed class KnockoutPlacementTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static School Sch(int id, double strength) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength,
    };

    private static List<School> MakeField(int count)
    {
        var list = new List<School>(count);
        for (var i = 0; i < count; i++) list.Add(Sch(i, 40 + i * 3.0));
        return list;
    }

    [Fact]
    public void FromMatches_ReturnsChampionFirst_AndEveryEntrantExactlyOnce()
    {
        var field = MakeField(8);
        var runner = TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(11), Schedule, "テスト地区大会");
        runner.DrainBracketResolution();

        var view = runner.BuildBracketView();
        var byId = field.ToDictionary(s => s.Id);
        var placement = KnockoutPlacement.FromMatches(view.Matches, id => byId[id]);

        Assert.Equal(8, placement.Count);
        Assert.Equal(placement.Select(s => s.Id).Distinct().Count(), placement.Count);
        Assert.Equal(view.ChampionName, placement[0].Name);
        // 準優勝＝決勝（RoundsRemaining==1）の敗者。
        var finalMatch = view.Matches.Single(m => m.RoundsRemaining == 1);
        Assert.Equal(finalMatch.LoserId, placement[1].Id);
    }

    [Fact]
    public void FromMatches_IsDeterministic()
    {
        List<School> Run(ulong seed)
        {
            var field = MakeField(8);
            var runner = TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(seed), Schedule, "テスト地区大会");
            runner.DrainBracketResolution();
            var byId = field.ToDictionary(s => s.Id);
            return KnockoutPlacement.FromMatches(runner.BuildBracketView().Matches, id => byId[id]).ToList();
        }

        var a = Run(7);
        var b = Run(7);
        Assert.Equal(a.Select(s => s.Id), b.Select(s => s.Id));
    }

    [Fact]
    public void FromMatches_TopN_CanBeUsedAsRegionalBerthCut()
    {
        var field = MakeField(16);
        var runner = TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(3), Schedule, "テスト地区大会");
        runner.DrainBracketResolution();

        var byId = field.ToDictionary(s => s.Id);
        var placement = KnockoutPlacement.FromMatches(runner.BuildBracketView().Matches, id => byId[id]);

        // 上位2校（地区大会の出場枠を想定）は元の16校の中に実在し、重複しない。
        var top2 = placement.Take(2).ToList();
        Assert.Equal(2, top2.Select(s => s.Id).Distinct().Count());
        Assert.All(top2, s => Assert.Contains(s.Id, field.Select(f => f.Id)));
    }

    [Fact]
    public void FromMatches_EmptyMatches_ReturnsEmpty()
        => Assert.Empty(KnockoutPlacement.FromMatches(System.Array.Empty<BracketMatch>(), _ => throw new System.Exception()));
}

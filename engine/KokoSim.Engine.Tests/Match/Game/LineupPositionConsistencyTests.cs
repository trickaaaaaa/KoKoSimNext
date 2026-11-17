using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 非DHの打順は「9人・守備位置が全部違う・投手の打順スロットの守備位置＝投手」を常に満たす（issue #1063）。
/// 交代パネルの打順表は打順の選手の守備位置で守備枠を引くので、ここが崩れると同じ選手が2行出て9番が消える。
/// </summary>
public sealed class LineupPositionConsistencyTests
{
    private static Player Field(string name, FieldPosition pos) => new() { Name = name, Position = pos };

    private static TeamState NonDh(IReadOnlyList<Player> reserves)
    {
        var order = new List<Player>
        {
            Field("捕", FieldPosition.Catcher), Field("一", FieldPosition.FirstBase),
            Field("二", FieldPosition.SecondBase), Field("三", FieldPosition.ThirdBase),
            Field("遊", FieldPosition.Shortstop), Field("左", FieldPosition.LeftField),
            Field("中", FieldPosition.CenterField), Field("右", FieldPosition.RightField),
            Field("先発", FieldPosition.Pitcher),
        };
        return new TeamState(new Team
        {
            Name = "T", BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(System.Array.Empty<Player>(), reserves),
        });
    }

    private static void AssertConsistent(TeamState st)
    {
        var lineup = st.CurrentLineup;
        Assert.Equal(9, lineup.Count);
        Assert.Equal(FieldPosition.Pitcher, lineup[st.PitcherSlot].Position);
        Assert.Equal(9, lineup.Select(p => p.Position).Distinct().Count());
        Assert.Equal(9, lineup.Select(p => p.Name).Distinct().Count());
        Assert.Equal(st.CurrentPitcher.Name, lineup[st.PitcherSlot].Name);
    }

    [Theory]
    [InlineData(FieldPosition.CenterField)]
    [InlineData(FieldPosition.Pitcher)]
    [InlineData(FieldPosition.FirstBase)]
    public void ReserveRelief_KeepsLineupConsistent(FieldPosition reservePos)
    {
        var st = NonDh(new[] { Field("村上", reservePos) });
        Assert.True(st.RelieveWith(st.Reserves[0]));
        AssertConsistent(st);
    }

    [Fact]
    public void ReserveRelief_ThenFielderSwap_KeepsLineupConsistent()
    {
        var st = NonDh(new[] { Field("村上", FieldPosition.CenterField) });
        Assert.True(st.RelieveWith(st.Reserves[0]));
        Assert.True(st.RelieveWith(st.CurrentLineup.First(p => p.Name == "中")));
        AssertConsistent(st);
    }

    [Fact]
    public void FielderSwap_KeepsLineupConsistent()
    {
        var st = NonDh(new[] { Field("控", FieldPosition.FirstBase) });
        Assert.True(st.RelieveWith(st.CurrentLineup.First(p => p.Name == "中"), allowBenchBackfill: false));
        AssertConsistent(st);
    }

    /// <summary>守備側の交代操作をどう並べても、打順の守備位置は崩れない（操作列を全探索する）。</summary>
    [Fact]
    public void AnyDefensiveOperationSequence_KeepsLineupConsistent()
    {
        var checkedSequences = 0;
        for (var code = 0; code < 6 * 6 * 6; code++)
        {
            var st = NonDh(new[]
            {
                Field("村上", FieldPosition.CenterField), Field("控一", FieldPosition.FirstBase),
                Field("控投", FieldPosition.Pitcher), Field("控遊", FieldPosition.Shortstop),
            });
            var c = code;
            for (var step = 0; step < 3; step++, c /= 6)
            {
                var op = c % 6;
                var lineup = st.CurrentLineup;
                switch (op)
                {
                    case 0: if (st.Reserves.Count > 0) st.RelieveWith(st.Reserves[0]); break;
                    case 1: if (st.Reserves.Count > 1) st.RelieveWith(st.Reserves[1], allowBenchBackfill: false); break;
                    case 2: st.RelieveWith(lineup.First(p => p.Position == FieldPosition.CenterField), allowBenchBackfill: false); break;
                    case 3: st.RelieveWith(lineup.First(p => p.Position == FieldPosition.Shortstop)); break;
                    case 4: if (st.Reserves.Count > 0) st.ConvertPitcherAndRelieve(FieldPosition.LeftField, st.Reserves[0]); break;
                    default: if (st.Reserves.Count > 0) st.AssignFielderToSlot(st.PitcherSlot == 0 ? 1 : 0, st.Reserves[^1]); break;
                }
                AssertConsistent(st);
            }
            checkedSequences++;
        }
        Assert.Equal(216, checkedSequences);
    }
}

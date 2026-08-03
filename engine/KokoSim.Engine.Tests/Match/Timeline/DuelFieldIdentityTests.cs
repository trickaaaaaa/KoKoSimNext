using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 対決ビューの背番号デカール・頭上フルネームラベル用の選手対応（issue #393）。
/// <see cref="DuelFieldIdentity.Build"/> は乱数不使用の純関数＝守備位置→選手を組むだけで試合結果に触れない。
/// </summary>
public sealed class DuelFieldIdentityTests
{
    private static Player At(FieldPosition pos, string name, int number)
        => new() { Position = pos, Name = name, UniformNumber = number };

    private static readonly Player RightFielder = At(FieldPosition.RightField, "右翼手", 9);
    private static readonly Player BenchRightFielder = At(FieldPosition.RightField, "控え右翼手", 15);

    private static TeamState MakeDefense()
    {
        var lineup = new List<Player>
        {
            At(FieldPosition.Catcher, "捕手", 2),
            At(FieldPosition.FirstBase, "一塁手", 3),
            At(FieldPosition.SecondBase, "二塁手", 4),
            At(FieldPosition.ThirdBase, "三塁手", 5),
            At(FieldPosition.Shortstop, "遊撃手", 6),
            At(FieldPosition.LeftField, "左翼手", 7),
            At(FieldPosition.CenterField, "中堅手", 8),
            RightFielder,
            At(FieldPosition.Pitcher, "投手", 1),
        };
        return new TeamState(new Team
        {
            Name = "テスト校", BattingOrder = lineup, PitcherSlot = 8,
            Reserves = new List<Player> { BenchRightFielder },
        });
    }

    [Fact]
    public void Build_MapsAllNineDefensePositionsToNameAndNumber()
    {
        var identity = DuelFieldIdentity.Build(MakeDefense());

        Assert.Equal(9, identity.Defense.Count);
        Assert.Equal(new DuelPlayerIdentity("投手", 1), identity.Defense[FieldPosition.Pitcher]);
        Assert.Equal(new DuelPlayerIdentity("捕手", 2), identity.Defense[FieldPosition.Catcher]);
        Assert.Equal(new DuelPlayerIdentity("遊撃手", 6), identity.Defense[FieldPosition.Shortstop]);
        Assert.Equal(new DuelPlayerIdentity("右翼手", 9), identity.Defense[FieldPosition.RightField]);
    }

    [Fact]
    public void Build_TracksSubstitution_WhenDefensiveSubChangesPosition()
    {
        var defense = MakeDefense();
        Assert.True(defense.DefensiveSub(RightFielder, BenchRightFielder));

        var identity = DuelFieldIdentity.Build(defense);

        Assert.Equal(new DuelPlayerIdentity("控え右翼手", 15), identity.Defense[FieldPosition.RightField]);
    }
}

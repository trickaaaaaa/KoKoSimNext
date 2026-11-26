using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 敵AIの「選手 × 守備位置」の評価を1本にそろえる（issue #1709）。試合前のスタメン配置（<see cref="AiTeamBuilder"/>）と
/// 試合中の守備位置の入替（<see cref="AiTacticsBrain"/>）が同じ <see cref="DefensePlacement"/> を通るので、
/// 顔ぶれが変わらなければスタメンの時点で最適＝終盤の入替は起きず、起きるのは途中交代で配置が崩れたときだけ。
/// </summary>
public sealed class DefensePlacementUnificationTests
{
    private static readonly int PositionCount = Enum.GetValues(typeof(FieldPosition)).Length;

    private static School MakeSchool(int id, double strength = 55)
        => new() { Id = id, Name = "架空東", PrefectureId = 13, Strength = strength, Fame = 45 };

    private static AiRosterDeps Deps() => new()
    {
        Persistent = new PersistentRosterCoefficients
        {
            Phenom = new PhenomCoefficients { SpikeRatePerSchoolYear = 0, AllRoundRatePerSchoolYear = 0 },
        },
    };

    /// <summary>改善が1ミリでもあれば入替を出す（閾値で隠さない）・采配ミスなし・ティアの関門なし。</summary>
    private static AiTacticsBrain StrictBrain(AiRosterDeps deps)
        => new(new AiProfile(100, 7, SchoolStyle.Standard),
            aiCoeff: deps.EnemyAi with { DefenseSwapMinGain = 1e-6 },
            fielding: deps.Fielding, roster: deps.Persistent);

    private static DefenseSwapSituation LateCloseSit(IReadOnlyList<Player> lineup)
        => new(9, 9, 0, lineup, lineup.Select(p => p.Position != FieldPosition.Pitcher).ToList());

    private static bool FiresOnAnySeed(AiTacticsBrain brain, DefenseSwapSituation s)
    {
        for (ulong seed = 1; seed <= 20; seed++)
            if (brain.CallDefenseSwap(s, new Xoshiro256Random(seed)) is not null) return true;
        return false;
    }

    // ===== 完了条件1: 同じ顔ぶれ・入替の条件を満たす場面で、入替を出さない =====

    [Theory]
    [InlineData(20.0)]
    [InlineData(55.0)]
    [InlineData(92.0)]
    public void StartingLineup_NeverTriggersLateSwap(double strength)
    {
        var deps = Deps();
        var brain = StrictBrain(deps);
        for (var id = 1; id <= 60; id++)
        {
            var school = MakeSchool(id, strength);
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);
            var team = AiTeamBuilder.Build(roster, school, 1, deps);

            Assert.False(FiresOnAnySeed(brain, LateCloseSit(team.BattingOrder)),
                $"校{id}(強さ{strength}): スタメンのまま7回以降の僅差で守備位置の入替が出た＝配置と入替の評価がずれている");
        }
    }

    // ===== 完了条件2: 途中交代で配置が崩れたら、統一した評価で改善する入替を出す =====

    private static Player Fielder(FieldPosition pos, string name, int fielding, int arm = 50, int[]? aptitudes = null)
        => new()
        {
            Position = pos, Name = name, Fielding = fielding, ArmStrength = arm,
            PositionAptitudes = aptitudes,
        };

    private static int[] Aptitudes(int baseValue, params (FieldPosition Pos, int Value)[] overrides)
    {
        var a = Enumerable.Repeat(baseValue, PositionCount).ToArray();
        foreach (var (pos, v) in overrides) a[(int)pos] = v;
        return a;
    }

    private static List<Player> Lineup(params Player[] slots) => slots.ToList();

    [Fact]
    public void BrokenPlacement_AfterSubstitution_SwapsToImprove()
    {
        var deps = Deps();
        var brain = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard),
            aiCoeff: deps.EnemyAi with { DefenseSwapMinGain = 1.0 });

        // 守備固めで遊撃に入った選手（守備30）と、一塁に居る上手い選手（守備80）。適性・肩は同じ。
        var lineup = Lineup(
            Fielder(FieldPosition.Catcher, "捕", 50),
            Fielder(FieldPosition.FirstBase, "一", 80),
            Fielder(FieldPosition.SecondBase, "二", 50),
            Fielder(FieldPosition.ThirdBase, "三", 50),
            Fielder(FieldPosition.Shortstop, "遊", 30),
            Fielder(FieldPosition.LeftField, "左", 50),
            Fielder(FieldPosition.CenterField, "中", 50),
            Fielder(FieldPosition.RightField, "右", 50),
            Fielder(FieldPosition.Pitcher, "投", 50));

        var pick = brain.CallDefenseSwap(LateCloseSit(lineup), new Xoshiro256Random(1));
        Assert.Equal((1, 4), pick);
    }

    [Fact]
    public void Swap_ReadsAptitude_NotJustRawFielding()
    {
        var deps = Deps();
        var brain = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard),
            aiCoeff: deps.EnemyAi with { DefenseSwapMinGain = 1.0 });

        // 一塁(守備80)は一塁以外の適性が低い(10)。他は守備50で適性も中庸＝今の配置が最適。
        // 素の守備力だけ見ると「上手い一塁手を重い位置へ」で入替が出てしまうが、適性込みなら出ない。
        var firstBaseApt = Aptitudes(10, (FieldPosition.FirstBase, 90));
        var lineup = Lineup(
            Fielder(FieldPosition.Catcher, "捕", 50),
            Fielder(FieldPosition.FirstBase, "一", 80, aptitudes: firstBaseApt),
            Fielder(FieldPosition.SecondBase, "二", 50),
            Fielder(FieldPosition.ThirdBase, "三", 50),
            Fielder(FieldPosition.Shortstop, "遊", 50),
            Fielder(FieldPosition.LeftField, "左", 50),
            Fielder(FieldPosition.CenterField, "中", 50),
            Fielder(FieldPosition.RightField, "右", 50),
            Fielder(FieldPosition.Pitcher, "投", 50));

        Assert.False(FiresOnAnySeed(brain, LateCloseSit(lineup)));

        // 同じ顔ぶれで適性を持たない（＝素の守備力だけで見る）なら、上手い一塁手を重い位置へ動かす入替が出る。
        var withoutAptitude = lineup.Select(p => p with { PositionAptitudes = null }).ToList();
        Assert.True(FiresOnAnySeed(brain, LateCloseSit(withoutAptitude)));
    }

    [Fact]
    public void Swap_ReadsArm_NotJustRawFielding()
    {
        var deps = Deps();
        var brain = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard),
            aiCoeff: deps.EnemyAi with { DefenseSwapMinGain = 1.0 });

        // 遊撃は守備が一塁より6低い（素の守備力だけなら 0.7×6=4.2 で入替が出る差）が、肩が圧倒的に強い。
        // 遊撃の肩の重み(0.6)が効く＝今の配置のほうが良い。
        var lineup = Lineup(
            Fielder(FieldPosition.Catcher, "捕", 50),
            Fielder(FieldPosition.FirstBase, "一", 56, arm: 30),
            Fielder(FieldPosition.SecondBase, "二", 50),
            Fielder(FieldPosition.ThirdBase, "三", 50),
            Fielder(FieldPosition.Shortstop, "遊", 50, arm: 80),
            Fielder(FieldPosition.LeftField, "左", 50),
            Fielder(FieldPosition.CenterField, "中", 50),
            Fielder(FieldPosition.RightField, "右", 50),
            Fielder(FieldPosition.Pitcher, "投", 50));

        Assert.False(FiresOnAnySeed(brain, LateCloseSit(lineup)));
    }

    // ===== 完了条件3: 試合前の配置で、適性と肩が同じなら守備力の高いほうが遊撃に入る =====

    [Fact]
    public void PreGamePlacement_BetterGloveGoesToShortstop_WhenAptitudeAndArmAreEqual()
    {
        var deps = Deps();
        var catcherApt = Aptitudes(50, (FieldPosition.Catcher, 20), (FieldPosition.Pitcher, 5));
        var players = new List<AiPlayer>();
        // Id=1 はエース（投手能力が同点なら Id 昇順で先頭）＝守備に就かない。
        for (var id = 1; id <= 11; id++)
        {
            var fielding = id switch { 2 => 80, 3 => 30, _ => 50 };
            var apt = id == 4 ? Aptitudes(50, (FieldPosition.Catcher, 95), (FieldPosition.Pitcher, 5)) : catcherApt;
            // 3番は守備が下手だが打てる（打撃で先発に残る）。守備は一塁へ回るはず。
            var bat = id == 3 ? 90 : 50;
            players.Add(new AiPlayer(id, 1, apt, PhenomType.None, 3,
                new Player { Fielding = fielding, Contact = bat, Power = bat, SourceId = id }));
        }
        var school = MakeSchool(1);
        var roster = new AiSchoolRoster(school.Id, players, 12);

        var team = AiTeamBuilder.Build(roster, school, 1, deps);

        Assert.Equal(2, team.BattingOrder.Single(p => p.Position == FieldPosition.Shortstop).SourceId);
        Assert.Equal(3, team.BattingOrder.Single(p => p.Position == FieldPosition.FirstBase).SourceId);

        // 同じ顔ぶれなら終盤の入替は出ない（完了条件1・2のつなぎ）。
        Assert.False(FiresOnAnySeed(StrictBrain(deps), LateCloseSit(team.BattingOrder)));
    }

    // ===== 評価そのものの単体確認 =====

    [Fact]
    public void Score_CombinesWeightAptitudeArmAndLeftPenalty()
    {
        var coeff = new EnemyAiCoefficients();
        var placement = new DefensePlacement(coeff);
        var p = new Player { Fielding = 60, ArmStrength = 40 };

        var neutral = placement.Score(p, 50, FieldPosition.Shortstop);
        var expected = coeff.DefenseWeightShortstop * AbilityScale.Phys(60)
                       + AbilityScale.Phys(40) * new PersistentRosterCoefficients().FieldSlotArmWeightShortstop;
        Assert.Equal(expected, neutral, 9);

        // 適性が高いほど評価が上がる。位置の重みが重いほど守備力の差が大きく効く。
        Assert.True(placement.Score(p, 90, FieldPosition.Shortstop) > neutral);
        Assert.True(placement.Score(p, 50, FieldPosition.Shortstop) - placement.Score(p, 50, FieldPosition.FirstBase)
                    > 0, "同じ選手でも重い位置のほうが評価が大きい");

        // 左投げは遊撃で罰（右投げ限定の位置）、一塁なら罰なし。
        var left = p with { Throws = Handedness.Left };
        Assert.True(placement.Score(left, 50, FieldPosition.Shortstop) < neutral - 1000.0);
        Assert.Equal(placement.Score(p, 50, FieldPosition.FirstBase), placement.Score(left, 50, FieldPosition.FirstBase), 9);
    }
}

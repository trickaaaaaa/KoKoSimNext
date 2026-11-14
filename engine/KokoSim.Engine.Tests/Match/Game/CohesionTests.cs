using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>チームの結束（設計書23 §3・issue #934）。U0 の式・ΔU の減衰・合宿・効き先（U=50 で恒等）。</summary>
public sealed class CohesionTests
{
    private static readonly CohesionCoefficients C = new();

    private static Player M(int leadership = 50, Personality type = Personality.Normal, int mental = 50)
        => new Player { Name = "m", Leadership = leadership, Mental = mental }.WithAxes(type);

    // ===== U0 =====

    [Fact]
    public void BaseValue_NeutralRosterIsFifty()
    {
        var members = Enumerable.Range(0, 20).Select(_ => M()).ToList();
        Assert.Equal(50.0, Cohesion.BaseValue(members, captain: null, C), 12);
        Assert.Equal(50.0, Cohesion.BaseValue(new List<Player>(), null, C), 12);
    }

    [Fact]
    public void BaseValue_FollowsDesignTerms()
    {
        var neutral = Enumerable.Range(0, 20).Select(_ => M()).ToList();
        // 主将の統率力（統率80×精神力50/100＝40）× b1
        var captain = M(leadership: 80);
        var withCaptain = new List<Player>(neutral.Skip(1)) { captain };
        Assert.Equal(50.0 + C.CaptainPowerWeight * 40 + C.LeaderWeight, Cohesion.BaseValue(withCaptain, captain, C), 12);

        // 引っ張る選手（統率65以上）は3人まで
        var leaders = neutral.Take(15).Concat(Enumerable.Range(0, 5).Select(_ => M(leadership: 70))).ToList();
        Assert.Equal(50.0 + 3 * C.LeaderWeight, Cohesion.BaseValue(leaders, null, C), 12);

        // 目立ちたがりの割合が半分を超えた分だけ減点（15/20＝0.75 → 0.25×b3）
        var selfish = Enumerable.Range(0, 15).Select(_ => M(type: Personality.Genius))
            .Concat(Enumerable.Range(0, 5).Select(_ => M(type: Personality.Hardworker))).ToList();
        Assert.Equal(50.0 - C.SelfishShareWeight * 0.25, Cohesion.BaseValue(selfish, null, C), 9);

        // 一匹狼（①↓②↓④目立ち）の人数ぶん減点
        var wolves = neutral.Take(18).Concat(new[] { M(30, Personality.LoneWolf), M(30, Personality.LoneWolf) }).ToList();
        Assert.Equal(50.0 - 2 * C.LoneWolfWeight, Cohesion.BaseValue(wolves, null, C), 12);
    }

    // ===== 効き先（U=50 で恒等） =====

    [Fact]
    public void Effects_AreIdentityAtFifty()
    {
        Assert.Equal(0, Cohesion.RattledThresholdStep(50, C));
        Assert.Equal(1.0, Cohesion.MessengerMultiplier(50, C), 12);
    }

    [Fact]
    public void Effects_FollowCohesion()
    {
        Assert.Equal(1, Cohesion.RattledThresholdStep(70, C));
        Assert.Equal(0, Cohesion.RattledThresholdStep(69.9, C));
        Assert.Equal(0, Cohesion.RattledThresholdStep(30, C));
        Assert.Equal(-1, Cohesion.RattledThresholdStep(29.9, C));
        Assert.Equal(1.06, Cohesion.MessengerMultiplier(80, C), 12);
        Assert.Equal(0.94, Cohesion.MessengerMultiplier(20, C), 12);
    }

    // ===== ΔU =====

    [Fact]
    public void Camp_RaisesCohesion_ThenDecaysBackOverWeeks()
    {
        var cal = new SeasonCalendar();
        var delta = 0.0;
        delta = Cohesion.AdvanceWeek(delta, cal.SummerCampWeek, cal, C);
        Assert.Equal(C.SummerCampDelta, delta, 12);
        var prev = delta;
        for (var w = cal.SummerCampWeek + 1; w < cal.SummerCampWeek + 20; w++)
        {
            delta = Cohesion.AdvanceWeek(delta, w, cal, C);
            Assert.True(delta < prev && delta > 0);
            prev = delta;
        }
        // 20週で 0.9^19 ≈ 13% まで戻る（1度の合宿の効果が1シーズン持続しない）
        Assert.InRange(delta / C.SummerCampDelta, 0.10, 0.16);

        // 冬合宿（強化）は夏より大きい
        Assert.Equal(C.WinterCampDelta, Cohesion.AdvanceWeek(0.0, cal.WinterCampWeek, cal, C), 12);
        Assert.True(C.WinterCampDelta > C.SummerCampDelta);
    }

    [Fact]
    public void Events_MoveDelta()
    {
        Assert.Equal(C.EventImproveDelta, Cohesion.AfterEvent(0, improved: true, C), 12);
        Assert.Equal(C.EventWorsenDelta, Cohesion.AfterEvent(0, improved: false, C), 12);
        Assert.Equal(100.0, Cohesion.Value(95, 30), 12);
        Assert.Equal(0.0, Cohesion.Value(5, -30), 12);
    }

    // ===== 試合への配線 =====

    private static Team TeamOf(string name, double delta = 0.0, Player? captain = null)
    {
        var slots = new[]
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };
        var order = slots.Select((pos, i) => new Player { Name = name + i, Position = pos }).ToList();
        order.Add(new Player
        {
            Name = name + "P", Position = FieldPosition.Pitcher,
            Pitching = new PitcherAttributes { MaxVelocityKmh = 135, Control = 50, StaminaPitches = 140, PitchRank = 50 },
        });
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, CohesionDelta = delta, Captain = captain };
    }

    [Fact]
    public void TeamState_CombinesBaseAndDelta()
    {
        Assert.Equal(50.0, new TeamState(TeamOf("A")).CohesionValue(C), 12);
        Assert.Equal(60.0, new TeamState(TeamOf("A", delta: 10)).CohesionValue(C), 12);
    }

    [Fact]
    public void NeutralTeam_PlaysTheSameGame()
    {
        // 全員中立・ΔU=0 のチームは U=50＝恒等。ΔU を入れた試合とは結果が変わりうるが、決定論は保つ。
        GameResult Play(double delta) => GameEngine.Play(TeamOf("A", delta), TeamOf("H"), new GameContext(), new Xoshiro256Random(5));
        var a = Play(0);
        var b = Play(0);
        Assert.Equal(a.TotalPitches, b.TotalPitches);
        Assert.Equal(a.AwayRuns, b.AwayRuns);
        var c = Play(40);
        var d = Play(40);
        Assert.Equal(c.TotalPitches, d.TotalPitches);
    }

    // ===== 保存 =====

    [Fact]
    public void Delta_RoundTripsThroughSave_AndOldSaveIsZero()
    {
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(new SaveState { CohesionDelta = 7.25 }), out var s, out var err), err);
        Assert.Equal(7.25, s.CohesionDelta, 12);

        var json = SaveCodec.Serialize(new SaveState()).Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":28")
            .Replace(",\"cohesionDelta\":0", "");
        Assert.True(SaveCodec.TryDeserialize(json, out var old, out var err2), err2);
        Assert.Equal(0.0, old.CohesionDelta);
    }
}

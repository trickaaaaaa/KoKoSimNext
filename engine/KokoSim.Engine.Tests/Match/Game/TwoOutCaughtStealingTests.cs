using System.Collections.Generic;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 2アウトの盗塁死（3アウト目）でも、その球は実際に投げ、盗塁の絵を載せた1球観測を出してから
/// 攻守交代する（issue #779）。旧実装は投球より前に半回を抜け、UI に1球も届かずいきなりチェンジになっていた。
/// <para>成否は投げた球で決まる: 見逃し・空振り・ボール（打席継続）なら盗塁死で3アウト。
/// ファウル・打球・打席を終わらせた球（三振・四球・死球）なら盗塁は成立しない（#626 と同じ巻き戻し）。</para>
/// </summary>
public sealed class TwoOutCaughtStealingTests
{
    public static IEnumerable<object[]> Targets() => new[]
    {
        new object[] { new ScenarioStart { Inning = 3, IsTop = true, Outs = 2, OnFirst = true }, StealTarget.Second },
        new object[] { new ScenarioStart { Inning = 3, IsTop = true, Outs = 2, OnSecond = true }, StealTarget.Third },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void TwoOuts_StealPitch_IsAlwaysObserved_AndCaughtStealingCarriesThePlay(ScenarioStart start, StealTarget target)
    {
        var caught = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true, CapturePitches = true }, seed, start);
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;

            prog.SetStealAttempt(offenseIsAway: true, StartType.Normal, target);
            var before = prog.LastPitch;
            prog.AdvancePitch();
            var thrown = prog.LastPitch;

            // 盗塁を予約した球は必ず投げられ、UI へ届く（旧実装はここが null/据え置きのまま攻守交代）
            Assert.NotNull(thrown);
            Assert.NotSame(before, thrown);

            if (thrown!.Outs < 3) continue;
            caught++;
            // 3アウト目の盗塁死: 盗塁の絵が載り、球は打席を続ける球（ファウル・打球・決着球ではない）
            Assert.NotNull(thrown.StealPlay);
            Assert.False(thrown.EndsPlateAppearance, $"seed {seed}: 打席を終わらせた球で盗塁死が成立している");
            Assert.True(thrown.Pitch.Kind is PitchKind.Ball or PitchKind.CalledStrike or PitchKind.SwingingStrike,
                $"seed {seed}: {thrown.Pitch.Kind} の球で盗塁死が成立している");
            Assert.False(thrown.BaseFirst || thrown.BaseSecond || thrown.BaseThird);   // 刺された走者は塁上にいない
        }
        Assert.True(caught > 0, "2アウトの盗塁死が一度も起きない（走者を遅くしてあるのに）");
    }

    [Fact]
    public void TwoOuts_CaughtStealing_CountsThePitch_AndBatterLeadsOffNextInning()
    {
        // 打席は未決着のまま半回を終える＝同じ打者が次の回の先頭。投げた球は投手の球数に数える。
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var start = new ScenarioStart { Inning = 3, IsTop = true, Outs = 2, OnFirst = true };
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true, CapturePitches = true }, seed, start);
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            var batter = prog.CurrentAtBat!.BatterOrder;
            prog.SetStealAttempt(offenseIsAway: true, StartType.Normal, StealTarget.Second);
            prog.AdvancePitch();
            if (prog.LastPitch is not { Outs: 3 }) continue;

            var snap = prog.Snapshot();
            Assert.Equal(1, snap.Home.Pitcher.Pitches);   // 先攻の攻撃＝後攻の投手が1球投げた

            // 裏の攻撃を流して、表の次の打席の頭まで進める
            while (prog.AdvancePitch() != AdvancePitchResult.Finished)
            {
                if (prog.CurrentAtBat is { IsTop: true, Inning: 4, PitchNumberInPa: 1 } next)
                {
                    Assert.Equal(batter, next.BatterOrder);
                    return;
                }
            }
            Assert.Fail("4回表に進まなかった");
        }
        Assert.Fail("2アウトの盗塁死が一度も起きない");
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 20, ArmStrength = 80, Fielding = 50, Catching = 50, Steal = 20,   // 走者は遅く・捕手は強肩
    };

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }
}

using System.Collections.Generic;
using System.Linq;
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
/// issue #1051: エンドランの空振りで一塁走者が憤死／進塁した球にも、盗塁と同じ走塁の再生データ
/// （<see cref="LivePitch.StealPlay"/>）を載せる。旧実装は成否の抽選だけで絵を作らず、2アウトなら
/// 空振りの絵のまま攻守交代していた。絵の秒は乱数を引かずに組む（成否の抽選・乱数の消費は不変）。
/// </summary>
public sealed class HitAndRunMissStealPlayTests
{
    private sealed record Seen(LiveAtBat Before, LivePitch Pitch);

    /// <summary>毎窓エンドランを予約して1試合回し、エンドランの空振りで一塁走者が塁を離れた球を集める。</summary>
    private static List<Seen> MissedHitAndRuns(ulong seed)
    {
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true, CapturePitches = true }, seed);
        var list = new List<Seen>();
        LiveAtBat? before = null;
        LivePitch? seen = null;
        while (true)
        {
            var r = prog.AdvancePitch();
            var lp = prog.LastPitch;
            if (lp is not null && !ReferenceEquals(lp, seen) && before is not null)
            {
                seen = lp;
                // 2アウトからの空振り三振は三振が3アウト目＝走者の憤死は起きない（走者は塁に残る）
                if (before.BaseFirst && lp.RunnerStarted(1) && lp.Pitch.Kind == PitchKind.SwingingStrike
                    && !lp.BaseFirst)
                    list.Add(new Seen(before, lp));
            }
            if (r == AdvancePitchResult.Finished) break;
            if (r == AdvancePitchResult.Pitch)
            {
                before = prog.CurrentAtBat;
                prog.SetOffensiveSign(true, OffensiveSign.HitAndRun);
                prog.SetOffensiveSign(false, OffensiveSign.HitAndRun);
            }
        }
        return list;
    }

    [Fact]
    public void MissedHitAndRun_CarriesTheRunnerPlay_ForOutAndSafe_AtAnyOuts()
    {
        var tag = new BaserunningCoefficients().TagSeconds;
        int thirdOut = 0, outBeforeTwo = 0, safe = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            foreach (var (before, lp) in MissedHitAndRuns(seed))
            {
                // 走塁の決着の絵が載っている（旧実装は null）
                Assert.NotNull(lp.StealPlay);
                var play = lp.StealPlay!;
                var runnerLeg = play.Runners.Single().Segs.Last();
                var throwArrive = play.Ball.Last().T1;   // 投球→捕手の送球の順＝最後が送球
                var caught = play.Result == "盗塁死";
                Assert.Equal(caught, runnerLeg.OutAtEnd);

                if (caught)
                {
                    // 絵と結果が食い違わない: 送球が走者より先に塁へ着く
                    Assert.True(throwArrive < runnerLeg.T1, $"seed {seed}: アウトなのに走者が先に着く");
                    Assert.True(lp.Outs > before.Outs);
                    if (lp.Outs >= 3) thirdOut++; else outBeforeTwo++;
                }
                else
                {
                    // セーフ: 走者がタッチより先に着く
                    Assert.True(runnerLeg.T1 < throwArrive + tag, $"seed {seed}: セーフなのに送球が先に着く");
                    Assert.True(lp.BaseSecond || lp.BaseThird);
                    safe++;
                }
            }
        }
        Assert.True(thirdOut > 0, "2アウトのエンドラン空振り憤死（3アウト目）が一度も起きない");
        Assert.True(outBeforeTwo > 0, "2アウト未満のエンドラン空振り憤死が一度も起きない");
        Assert.True(safe > 0, "エンドラン空振りで走者が二塁へ進む場面が一度も起きない");
    }

    [Fact]
    public void TwoOuts_MissedHitAndRun_ThirdOut_IsObservedWithThePlay()
    {
        // 2アウト一塁: 空振り（三振ではない）で憤死＝3アウト目。この球の観測に走塁の絵が載ってから半回が終わる。
        var start = new ScenarioStart { Inning = 3, IsTop = true, Outs = 2, OnFirst = true };
        var caught = 0;
        for (ulong seed = 1; seed <= 80; seed++)
        {
            var prog = new MatchProgression(Team("A", slowRunners: true), Team("H", slowRunners: true),
                new GameContext { CaptureTimelines = true, CapturePitches = true }, seed, start);
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetOffensiveSign(true, OffensiveSign.HitAndRun);
            prog.AdvancePitch();
            var lp = prog.LastPitch;
            if (lp is not { Outs: 3 } || lp.Pitch.Kind != PitchKind.SwingingStrike || lp.EndsPlateAppearance) continue;
            caught++;
            Assert.NotNull(lp.StealPlay);
            Assert.Equal("盗塁死", lp.StealPlay!.Result);
        }
        Assert.True(caught > 0, "2アウトのエンドラン空振り憤死が一度も起きない");
    }

    private static Player Pos(FieldPosition pos, bool slow) => new()
    {
        Position = pos, Contact = 30, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = slow ? 20 : 60, ArmStrength = slow ? 80 : 50, Fielding = 50, Catching = 50, Steal = slow ? 20 : 50,
    };

    private static Team Team(string name, bool slowRunners = false)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher, slowRunners), Pos(FieldPosition.FirstBase, slowRunners),
            Pos(FieldPosition.SecondBase, slowRunners), Pos(FieldPosition.ThirdBase, slowRunners),
            Pos(FieldPosition.Shortstop, slowRunners), Pos(FieldPosition.LeftField, slowRunners),
            Pos(FieldPosition.CenterField, slowRunners), Pos(FieldPosition.RightField, slowRunners),
            Pos(FieldPosition.Pitcher, slowRunners) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }
}

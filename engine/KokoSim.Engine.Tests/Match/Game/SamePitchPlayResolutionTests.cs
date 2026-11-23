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
/// issue #1206: 同じ1球の「走者のプレー」と「打席の決着／3アウト」が噛み合うこと。
/// 場面ジャンプ（<see cref="ScenarioStart"/>）で狙った局面の初球を作り、その1球の結果だけを見る。
/// 暴投・振り逃げ・重盗の確率は1.0へ振って「起きるなら必ず起きる」状態にしてから、規則どおりの結末を固定する。
/// </summary>
public sealed class SamePitchPlayResolutionTests
{
    private static readonly BaserunningCoefficients C = new();

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50 - AbilityScale.Delta, Power = 50 - AbilityScale.Delta, LaunchTendency = 50, Discipline = 50 - AbilityScale.Delta,
        Speed = 60 - AbilityScale.Delta, ArmStrength = 50 - AbilityScale.Delta, Fielding = 50 - AbilityScale.Delta, Catching = 50 - AbilityScale.Delta,
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
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(
                new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
                System.Array.Empty<Player>()),
        };
    }

    private static GameResult Run(MatchProgression prog)
    {
        while (prog.AdvancePitch() != AdvancePitchResult.Finished) { }
        return prog.BuildResult();
    }

    /// <summary>打席が <paramref name="pas"/> 本確定する（または打席途中の3アウトで裏へ移る）まで進める。試合を最後まで回さない。</summary>
    private static (IReadOnlyList<PlayLogEntry> Log, int AwayScore) PlayFirstPas(MatchProgression prog, int pas = 1)
    {
        while (prog.AdvancePitch() != AdvancePitchResult.Finished)
            if (prog.ConfirmedPlateAppearances >= pas || prog.CurrentAtBat is { IsTop: false }) break;
        return (prog.BuildResult().Log, prog.AwayScore);
    }

    /// <summary>暴投・振り逃げが「後逸しうる球なら必ず起きる」状態にする係数（コースのゲートは残る）。</summary>
    private static GameContext AlwaysMissCtx(double dropReach) => new()
    {
        Baserunning = C with
        {
            WildPitchProb = 1.0, PassedBallProb = 0.0,
            DropThirdStrikeReachProb = dropReach, DropThirdStrikeCatchingSlope = 0.0,
        },
    };

    private static MatchProgression TwoStrikeScenario(GameContext ctx, ulong seed, int outs, bool first, bool second, bool third)
        => new(Team("A"), Team("H"), ctx, seed, new ScenarioStart
        {
            Inning = 1, IsTop = true, Outs = outs, OnFirst = first, OnSecond = second, OnThird = third, Strikes = 2,
        });

    private static bool IsFirstPitchStrikeout(PlayLogEntry e)
        => e is { IsTop: true, Inning: 1, Result: PlateAppearanceResult.Strikeout, Pitches: 1 };

    /// <summary>1. 2アウト三塁で第3ストライクが暴投でも、三振が3アウト目＝三塁走者の得点は残らない。</summary>
    [Fact]
    public void ThirdStrikeAtTwoOuts_WildPitch_DoesNotScoreRunnerFromThird()
    {
        var strikeouts = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var (log, away) = PlayFirstPas(TwoStrikeScenario(AlwaysMissCtx(dropReach: 0.0), seed, outs: 2, first: false, second: false, third: true));
            if (log.Count == 0 || !IsFirstPitchStrikeout(log[0])) continue;
            strikeouts++;
            Assert.Equal(0, away);
        }
        Assert.True(strikeouts >= 20, $"初球三振のシードが足りない（{strikeouts} 件）");
    }

    /// <summary>1'. 満塁でも、2アウトの三振が3アウト目なら暴投の進塁・得点は残らない。</summary>
    [Fact]
    public void ThirdStrikeAtTwoOuts_BasesLoaded_NoRunScores()
    {
        var strikeouts = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var (log, away) = PlayFirstPas(TwoStrikeScenario(AlwaysMissCtx(dropReach: 0.0), seed, outs: 2, first: true, second: true, third: true));
            if (log.Count == 0 || !IsFirstPitchStrikeout(log[0])) continue;
            strikeouts++;
            Assert.Equal(0, away);
        }
        Assert.True(strikeouts >= 20, $"初球三振のシードが足りない（{strikeouts} 件）");
    }

    /// <summary>1''. 一塁が空いている三振では、暴投と振り逃げが同じ1球で二重に走者を進めない（二塁走者は三塁止まり・生還しない）。</summary>
    [Fact]
    public void ThirdStrikeWithFirstOpen_WildPitchAndDropThirdStrike_AdvanceRunnersOnlyOnce()
    {
        var dropped = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var (log, _) = PlayFirstPas(TwoStrikeScenario(AlwaysMissCtx(dropReach: 1.0), seed, outs: 0, first: false, second: true, third: false));
            if (log.Count == 0 || !IsFirstPitchStrikeout(log[0])) continue;
            var pa = log[0];
            Assert.Equal(0, pa.RunsScored);
            if (pa.OutsThisPa == 0)
            {
                dropped++;
                Assert.True(pa.BaseFirstAfter && pa.BaseThirdAfter && !pa.BaseSecondAfter, $"seed {seed}: 振り逃げ後の塁が一塁・三塁でない");
            }
        }
        Assert.True(dropped >= 3, $"振り逃げが起きたシードが足りない（{dropped} 件）");
    }

    /// <summary>1'''. 一塁走者あり・2アウト未満の三振は打者アウト＝暴投で走者が進むのは1回だけ（振り逃げは重ならない）。</summary>
    [Fact]
    public void ThirdStrikeWithFirstOccupied_WildPitchAdvancesOnce_BatterStaysOut()
    {
        var advanced = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var (log, _) = PlayFirstPas(TwoStrikeScenario(AlwaysMissCtx(dropReach: 1.0), seed, outs: 1, first: true, second: false, third: false));
            if (log.Count == 0 || !IsFirstPitchStrikeout(log[0])) continue;
            var pa = log[0];
            Assert.Equal(1, pa.OutsThisPa);                           // 打者はアウトのまま
            Assert.False(pa.BaseThirdAfter, $"seed {seed}: 同じ1球で走者が2つ進んだ");
            Assert.NotEqual(pa.BaseFirstAfter, pa.BaseSecondAfter);   // 走者はちょうど1人（一塁に残る or 二塁へ進む）
            if (pa.BaseSecondAfter) advanced++;
        }
        Assert.True(advanced >= 5, $"暴投で進塁したシードが足りない（{advanced} 件）");
    }

    /// <summary>2. 一・三塁の重盗で一塁走者の盗塁死が3アウト目なら、三塁走者の得点は残らない。</summary>
    [Fact]
    public void DoubleSteal_CaughtForThirdOut_DoesNotScoreRunnerFromThird()
    {
        var caught = 0;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var ctx = new GameContext
            {
                Baserunning = C with { DoubleStealThirdBreakProb = 1.0 },
                CaptureTimelines = true, CapturePitches = true,
            };
            var prog = new MatchProgression(Team("A"), Team("H"), ctx, seed, new ScenarioStart
            {
                Inning = 1, IsTop = true, Outs = 2, OnFirst = true, OnThird = true,
            });
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetStealAttempt(offenseIsAway: true, StartType.Normal, StealTarget.Second);
            prog.AdvancePitch();
            var lp = prog.LastPitch;
            if (lp is null || lp.Outs < 3) continue;   // 盗塁死で3アウト目になった球だけが対象
            caught++;
            var (_, away) = PlayFirstPas(prog);
            Assert.Equal(0, away);
        }
        Assert.True(caught >= 10, $"盗塁死で3アウトになったシードが足りない（{caught} 件）");
    }

    /// <summary>3. 2アウトのスクイズは、打者が一塁でアウト（3アウト目）なら三塁走者の得点を認めない（規則5.08(a)）。</summary>
    [Fact]
    public void Squeeze_BatterOutForThirdOut_DoesNotScoreRunnerFromThird()
    {
        var thirdOuts = 0;
        for (ulong seed = 1; seed <= 300; seed++)
        {
            // ウエスト（ボール球）での暴投の得点が混ざらないよう、暴投・捕逸は切る。
            var ctx = new GameContext { Baserunning = C with { WildPitchProb = 0.0, PassedBallProb = 0.0 } };
            var prog = new MatchProgression(Team("A"), Team("H"), ctx, seed, new ScenarioStart
            {
                Inning = 1, IsTop = true, Outs = 2, OnThird = true,
            });
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Squeeze);
            var (log, away) = PlayFirstPas(prog);
            if (log.Count == 0 || log[0] is not { IsTop: true, Inning: 1, OutsBefore: 2, OutsThisPa: >= 1 } pa) continue;
            thirdOuts++;
            Assert.Equal(0, pa.RunsScored);
            Assert.Equal(0, away);
        }
        Assert.True(thirdOuts >= 10, $"スクイズで3アウト目になったシードが足りない（{thirdOuts} 件）");
    }

    /// <summary>4. 2アウトからでも、1打席で増えるアウトは3アウト目までで頭打ち（4アウト目を記録しない）。</summary>
    [Theory]
    [InlineData(PitchBattingOverride.Bunt, true, false)]       // 小フライ併殺（一塁走者）
    [InlineData(PitchBattingOverride.Squeeze, true, true)]     // 打者アウト＋三塁走者の本塁憤死
    [InlineData(PitchBattingOverride.Squeeze, false, true)]
    public void OutsInOnePa_NeverExceedThree(PitchBattingOverride kind, bool first, bool third)
    {
        var thirdOuts = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"), new GameContext(), seed, new ScenarioStart
            {
                Inning = 1, IsTop = true, Outs = 2, OnFirst = first, OnThird = third,
            });
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetPitchBattingOverride(offenseIsAway: true, kind);
            var (log, _) = PlayFirstPas(prog);
            foreach (var e in log)
                Assert.True(e.OutsBefore + e.OutsThisPa <= 3, $"seed {seed}: {e.OutsBefore}死から{e.OutsThisPa}アウト");
            if (log.Count > 0 && log[0].OutsBefore == 2 && log[0].OutsThisPa == 1) thirdOuts++;
        }
        Assert.True(thirdOuts > 0, "2アウトから3アウト目が記録された打席が一度も無い（検証になっていない）");
    }

    /// <summary>5. 四球の球での盗塁死は成立しない（一塁走者には二塁への安全進塁権がある）。</summary>
    [Fact]
    public void FourthBall_CaughtStealingIsRolledBack()
    {
        var walks = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"), new GameContext { CaptureTimelines = true, CapturePitches = true }, seed,
                new ScenarioStart { Inning = 1, IsTop = true, Outs = 0, OnFirst = true, Balls = 3 });
            if (prog.AdvancePitch() != AdvancePitchResult.Pitch) continue;
            prog.SetStealAttempt(offenseIsAway: true, StartType.Gamble, StealTarget.Second);
            var (log, _) = PlayFirstPas(prog, pas: 2);
            if (log.Count < 2 || log[0] is not { IsTop: true, Inning: 1, Result: PlateAppearanceResult.Walk, Pitches: 1 } pa) continue;
            walks++;
            Assert.Equal(0, pa.OutsThisPa);
            Assert.True(pa.BaseFirstAfter && pa.BaseSecondAfter, $"seed {seed}: 四球で一・二塁になっていない");
            Assert.Equal(0, log[1].OutsBefore);   // 盗塁死のアウトが残っていない
        }
        Assert.True(walks >= 10, $"初球四球のシードが足りない（{walks} 件）");
    }

    /// <summary>6. サヨナラで試合が終わった後に継投判断をしない（PitcherChanges／継投ログに残さない）。</summary>
    [Fact]
    public void WalkOff_DoesNotChangePitcherAfterGameEnds()
    {
        // 半回の頭の継投判断では変えず、この打席の球数を足した後でだけ継投したくなる疲労にする。
        var threshold = Pos(FieldPosition.Pitcher).Pitching.StaminaPitches + new GameContext().Fatigue.RelievePitchMargin;
        var fatigue = (int)System.Math.Ceiling(threshold) - 1;

        MatchProgression Start(ulong seed, int awayScore) => new(Team("A"), Team("H"), new GameContext(), seed, new ScenarioStart
        {
            Inning = 9, IsTop = false, Outs = 1, OnThird = true, AwayScore = awayScore, HomeScore = 0,
            PitcherFatiguePitches = fatigue,
        });

        var walkOffs = 0;
        var controlChanges = 0;
        for (ulong seed = 1; seed <= 80; seed++)
        {
            var r = Run(Start(seed, awayScore: 0));
            if (r.Log.Count != 1 || r.HomeRuns <= r.AwayRuns) continue;   // 最初の打席で決着したサヨナラだけ
            walkOffs++;
            Assert.Equal(0, r.PitcherChanges);
            Assert.Empty(r.PitchingChangeLog);

            // 同じ打席でも、勝ち越さない（試合が続く）なら疲れた投手は交代する＝前提（疲労で継投する状態）。
            var control = Run(Start(seed, awayScore: 5));
            if (control.PitchingChangeLog.Any(e => e is { Inning: 9, IsTopHalf: false })) controlChanges++;
        }
        Assert.True(walkOffs >= 3, $"初打席サヨナラのシードが足りない（{walkOffs} 件）");
        Assert.True(controlChanges > 0, "疲労しても継投しない（前提が崩れている）");
    }
}

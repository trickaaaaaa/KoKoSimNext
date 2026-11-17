using System;
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
/// 打席の途中の走者アウト（盗塁死・牽制死・スクイズ挟殺・エンドラン憤死）で3アウトになった直後、
/// 次の半回の最初の打席の采配窓で交代パネルの攻守判定が正しいこと（issue #1050）。
/// 旧実装は局面スナップショット（塁・アウト・表裏）を打席の決着時にしか更新せず、打席未決着で半回を
/// 抜ける経路では前の半回のまま残っていた＝守備側が「攻撃中」と判定され代打・代走しか出せなかった。
/// </summary>
public sealed class MidPaThirdOutSideJudgementTests
{
    private static readonly ScenarioStart TwoOutsOnFirst = new() { Inning = 3, IsTop = true, Outs = 2, OnFirst = true };

    [Fact]
    public void CaughtStealing_ThirdOut_NextHalfFirstPitchWindow_JudgesSidesCorrectly()
    {
        var seen = RunUntilMidPaThirdOut(TwoOutsOnFirst, prog =>
            prog.SetStealAttempt(offenseIsAway: true, StartType.Normal, StealTarget.Second));
        Assert.True(seen > 0, "2アウトの盗塁死が一度も起きない");
    }

    [Fact]
    public void PickedOff_ThirdOut_NextHalfFirstPitchWindow_JudgesSidesCorrectly()
    {
        var seen = RunUntilMidPaThirdOut(TwoOutsOnFirst, prog => prog.ForceNext(ForcedOutcome.PickoffOut),
            forceBeforeFirstWindow: true);
        Assert.True(seen > 0, "2アウトの牽制死が一度も起きない");
    }

    [Fact]
    public void SqueezeRunnerCaught_ThirdOut_NextHalfFirstPitchWindow_JudgesSidesCorrectly()
    {
        var start = new ScenarioStart { Inning = 3, IsTop = true, Outs = 2, OnThird = true };
        var seen = RunUntilMidPaThirdOut(start, prog => prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Squeeze),
            seeds: 200);
        Assert.True(seen > 0, "2アウトのスクイズ挟殺が一度も起きない");
    }

    [Fact]
    public void BottomHalf_CaughtStealing_ThirdOut_NextInningTopWindow_JudgesSidesCorrectly()
    {
        // 裏の攻撃が打席途中で終わる＝次は次の回の表（回またぎでも攻守が正しく入れ替わる）。
        var start = new ScenarioStart { Inning = 3, IsTop = false, Outs = 2, OnFirst = true };
        var seen = RunUntilMidPaThirdOut(start, prog =>
            prog.SetStealAttempt(offenseIsAway: false, StartType.Normal, StealTarget.Second));
        Assert.True(seen > 0, "2アウトの盗塁死が一度も起きない");
    }

    [Fact]
    public void RegularThirdOut_NextHalfFirstPitchWindow_StillJudgesSidesCorrectly()
    {
        // 打席の決着で取った3アウト（従来から正しい経路）も変わらないこと。
        var start = new ScenarioStart { Inning = 3, IsTop = true, Outs = 2 };
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true, CapturePitches = true }, 1, start);
        prog.ForceNext(ForcedOutcome.Strikeout);
        while (prog.AdvancePitch() is var r && r != AdvancePitchResult.Finished)
        {
            if (prog.CurrentAtBat is { IsTop: false, Inning: 3 }) break;
        }
        AssertDefenseIs(prog, awayIsDefending: true);
    }

    [Fact]
    public void BeforeFirstPitch_AwayIsOnOffense()
    {
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true, CapturePitches = true }, 1);
        Assert.True(prog.SubstitutionOptions(teamIsAway: true).IsOffense);
        Assert.False(prog.SubstitutionOptions(teamIsAway: false).IsOffense);
    }

    /// <summary>
    /// 最初の打席の采配窓で <paramref name="arm"/>（盗塁予約等）を仕掛けて1球進め、その球で打席未決着のまま
    /// 3アウトになったら、次の半回の最初の打席の最初の采配窓まで進めて攻守判定を確かめる。成立した回数を返す。
    /// </summary>
    private static int RunUntilMidPaThirdOut(ScenarioStart start, Action<MatchProgression> arm,
        bool forceBeforeFirstWindow = false, int seeds = 60)
    {
        var seen = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"),
                new GameContext { CaptureTimelines = true, CapturePitches = true }, seed, start);
            if (forceBeforeFirstWindow) arm(prog);
            var armed = forceBeforeFirstWindow;

            // 最初の打席が決着しないまま半回が変わったか（＝打席途中の走者アウトで3アウト）。
            var midPaThirdOut = false;
            var windows = 0;
            while (prog.AdvancePitch() == AdvancePitchResult.Pitch)
            {
                var ab = prog.CurrentAtBat!;
                if (ab.IsTop == start.IsTop && ab.Inning == start.Inning)
                {
                    if (windows++ > 0 && ab.PitchNumberInPa == 1) break;   // 最初の打席が決着した＝対象外
                    if (!armed) { arm(prog); armed = true; }
                    continue;   // 同じ打席の続き
                }
                // 半回が変わった最初の窓: 直前の球が打席を終わらせていなければ打席途中の3アウト
                // （牽制死は球を投げない＝LastPitch なし。スクイズ挟殺の球は挟殺前のアウト数で観測される）。
                midPaThirdOut = ab.PitchNumberInPa == 1
                    && (prog.LastPitch is null || prog.LastPitch is { EndsPlateAppearance: false });
                break;
            }
            if (!midPaThirdOut) continue;
            var next = prog.CurrentAtBat!;
            Assert.NotEqual(start.IsTop, next.IsTop);

            seen++;
            // 次の半回で守備に就くのは、前の半回で攻撃していた側（表の攻撃＝先攻 away）。
            AssertDefenseIs(prog, awayIsDefending: start.IsTop);
        }
        return seen;
    }

    private static void AssertDefenseIs(MatchProgression prog, bool awayIsDefending)
    {
        var defense = prog.SubstitutionOptions(teamIsAway: awayIsDefending);
        var offense = prog.SubstitutionOptions(teamIsAway: !awayIsDefending);
        Assert.False(defense.IsOffense, "守備側が攻撃中と判定されている");
        Assert.True(offense.IsOffense, "攻撃側が守備中と判定されている");
        Assert.False(prog.IsTeamOnOffense(awayIsDefending));
        Assert.True(prog.IsTeamOnOffense(!awayIsDefending));
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 20, ArmStrength = 80, Fielding = 50, Catching = 50, Steal = 20,   // 走者は遅く・捕手は強肩
        Lead = 90,
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

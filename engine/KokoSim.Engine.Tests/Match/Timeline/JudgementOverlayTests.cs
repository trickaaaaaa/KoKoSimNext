using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 判定オーバーレイ（アウト/セーフ, Issue #59）: margin[s]をTimeline→Playbackへ貫通させ、
/// 「際どいセーフだけ表示」のゲーティングが正しく効くこと（描画専用・決定論/帯には影響しない）。
/// </summary>
public sealed class JudgementOverlayTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly Aerodynamics Aero = new();

    // --- FieldingResolver: 一塁の判定marginは送球有無・符号がOut/Singleの帰結と一致する ---

    [Fact]
    public void FieldingResolver_InfieldGrounder_JudgementMargin_PresentIffThrowArrived_AndSignMatchesOutcome()
    {
        // 送球が絡む内野ゴロ・内野安打を幅広い当たりで走査し、margin の有無・符号が
        // 既存の判定（ThrowArriveSeconds有無・Result）と矛盾しないことを検証する（Resolveのif文自体は不変）。
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0 };
        var checkedOut = false;
        var checkedSingle = false;
        for (var velo = 5.0; velo <= 45.0; velo += 0.5)
        {
            var ball = new BattedBall { ExitVelocityMps = velo, LaunchAngleDeg = -3, BearingDeg = 5 };
            var play = FieldingResolver.ResolveDetailed(
                ball, Field, Aero, BatterAttributes.LeagueAverage, Field.StandardAlignment(), coeff, new Xoshiro256Random(1));

            if (!play.ThrowArriveSeconds.HasValue)
            {
                Assert.Null(play.JudgementMarginSeconds); // 送球が絡まないプレーはmargin無し
                continue;
            }

            Assert.NotNull(play.JudgementMarginSeconds); // 送球が絡んだ判定は必ずmarginを持つ
            if (play.Result == BattedBallResult.Out) { Assert.True(play.JudgementMarginSeconds <= 0); checkedOut = true; }
            else if (play.Result == BattedBallResult.Single) { Assert.True(play.JudgementMarginSeconds > 0); checkedSingle = true; }
        }
        Assert.True(checkedOut, "内野ゴロのアウトが走査範囲に無い（テスト条件を見直す必要）");
        Assert.True(checkedSingle, "内野安打が走査範囲に無い（テスト条件を見直す必要）");
    }

    // --- FieldingResolver: 飛球の捕球余裕(CatchMarginSeconds)は符号がOut/Errorと安打の帰結に一致する ---

    [Fact]
    public void FieldingResolver_Fly_CatchMargin_PresentIffFly_AndSignMatchesOutcome()
    {
        // 深さの異なるフライを幅広く走査し、捕球余裕の有無・符号が既存の判定（IsFly・Result）と
        // 矛盾しないことを検証する（Issue #328②。Resolveのif文自体は不変）。
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0 };
        var checkedCaught = false;
        var checkedDropped = false;
        for (var velo = 15.0; velo <= 45.0; velo += 0.5)
        {
            var ball = new BattedBall { ExitVelocityMps = velo, LaunchAngleDeg = 30, BearingDeg = 0 };
            var play = FieldingResolver.ResolveDetailed(
                ball, Field, Aero, BatterAttributes.LeagueAverage, Field.StandardAlignment(), coeff, new Xoshiro256Random(1));

            if (!play.IsFly)
            {
                Assert.Null(play.CatchMarginSeconds); // ゴロ・本塁打・ファウルはmargin無し
                continue;
            }

            Assert.NotNull(play.CatchMarginSeconds); // フライ/ライナーは必ずmarginを持つ
            if (play.Result is BattedBallResult.Out or BattedBallResult.Error)
            {
                Assert.True(play.CatchMarginSeconds >= 0); checkedCaught = true;
            }
            else
            {
                Assert.True(play.CatchMarginSeconds < 0); checkedDropped = true;
            }
        }
        Assert.True(checkedCaught, "捕球アウトが走査範囲に無い（テスト条件を見直す必要）");
        Assert.True(checkedDropped, "落球（安打）が走査範囲に無い（テスト条件を見直す必要）");
    }

    // --- TimelineBuilder.BuildBattedBall: CloseCall はしきい値駆動で、アウト表示自体には影響しない ---

    // margin は timing から導けるが、ここではしきい値ロジックだけを切り出して検査したいので明示で渡す。
    private static FieldingPlay InfieldSingle(double judgementMargin)
        => FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.Shortstop, throwArriveSeconds: 4.19,
            result: BattedBallResult.Single, judgementMarginSeconds: judgementMargin);

    private static FieldingPlay InfieldOut(double judgementMargin)
        => FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.Shortstop, throwArriveSeconds: 3.0,
            judgementMarginSeconds: judgementMargin);

    private static RunnerLeg BatterLeg(PlayTimeline tl) => tl.Runners.Single(r => r.Label == "打");

    [Fact]
    public void BuildBattedBall_CloseInfieldHit_IsCloseCall_WhenMarginBelowThreshold()
    {
        var play = InfieldSingle(judgementMargin: 0.05); // 送球到達とほぼ同時＝際どい内野安打
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15);

        var leg = BatterLeg(tl);
        Assert.False(leg.OutAtEnd);
        Assert.True(leg.CloseCall, "しきい値内のmarginなのにCloseCallがfalse");
    }

    [Fact]
    public void BuildBattedBall_RoutineInfieldHit_IsNotCloseCall_WhenMarginExceedsThreshold()
    {
        var play = InfieldSingle(judgementMargin: 0.9); // 悠々間に合わず＝明白なセーフ
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15);

        var leg = BatterLeg(tl);
        Assert.False(leg.OutAtEnd);
        Assert.False(leg.CloseCall, "明白なセーフなのにCloseCallがtrue");
    }

    [Fact]
    public void BuildBattedBall_ForceOut_AlwaysHasOutAtEnd_RegardlessOfCloseCallThreshold()
    {
        // アウトは常時表示（CloseCallしきい値に関わらず）。OutAtEndはCloseCallの影響を受けない。
        var play = InfieldOut(judgementMargin: -0.9); // 明白なアウト
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15);

        Assert.True(BatterLeg(tl).OutAtEnd);
    }

    [Fact]
    public void BuildBattedBall_UncontestedPlay_NeverCloseCall_EvenWithHugeThreshold()
    {
        // 送球が絡まないプレー（フライアウト等）はJudgementMarginSeconds=null＝どんなしきい値でもCloseCallにならない。
        var play = FieldingPlayFixtures.CaughtFly(
            FieldPosition.CenterField, landingZ: 90, hangTimeSeconds: 3.0, apexHeightM: 20);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 1000.0);

        Assert.False(BatterLeg(tl).CloseCall);
    }

    // --- 判定イベント列（Issue #260）: フライは捕球の瞬間、内野ゴロは決着レッグ終端で1件だけ確定する ---

    [Fact]
    public void BuildBattedBall_FlyOut_JudgementFiresAtFieldedAt_NotAtBatterArrival()
    {
        // フライ・ライナーの捕球アウトは、打者走者が一塁へ走り切る時刻(BatterToFirstSeconds)ではなく
        // 捕球の瞬間(FieldedAtSeconds)に判定が確定する（症状1＋2の回帰）。
        var play = FieldingPlayFixtures.CaughtFly(
            FieldPosition.CenterField, landingZ: 90, hangTimeSeconds: 3.0, apexHeightM: 20);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15);

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Out, j.Kind);
        Assert.Equal("打", j.Label);
        Assert.Equal(ContactAtSeconds + play.FieldedAtSeconds!.Value, j.T, 6);
        Assert.NotEqual(ContactAtSeconds + play.BatterToFirstSeconds, j.T, 6);
    }

    [Fact]
    public void BuildBattedBall_GroundOut_JudgementFiresOnce_AtThrowArrival()
    {
        // Issue #328①: 送球が塁に届いた瞬間にアウトが確定する（走者到達を待たない）。
        var play = InfieldOut(judgementMargin: -0.9);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15);

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Out, j.Kind);
        Assert.Equal(ContactAtSeconds + play.ThrowArriveSeconds!.Value, j.T, 6);
        Assert.True(j.T < BatterLeg(tl).T1, "判定は走者到達より前のはず（走者は判定確定後も一塁まで走り切る）");
    }

    [Fact]
    public void BuildBattedBall_CloseInfieldHit_JudgementFiresOnce_AsSafe()
    {
        var play = InfieldSingle(judgementMargin: 0.05);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15);

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Safe, j.Kind);
        Assert.True(j.CloseCall);
        Assert.Equal(BatterLeg(tl).T1, j.T, 6);
    }

    // --- 判定イベント「フェア」（Issue #328②）: 際どい落球だけ着地の瞬間に確定する ---

    private static FieldingPlay DroppedFly(double catchMargin) => FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: 90, fieldedAtSeconds: 3.6,
            result: BattedBallResult.Single, hangTimeSeconds: 3.5, apexHeightM: 25.0, isFly: true)
        with { CatchMarginSeconds = catchMargin };

    [Fact]
    public void BuildBattedBall_CloseDroppedFly_JudgementFiresAsFair_AtLandAt()
    {
        var play = DroppedFly(catchMargin: -0.05); // 僅かに追いつけず落球＝際どい
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15, closeFlyMarginSeconds: 0.15);

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Fair, j.Kind);
        Assert.Equal("打", j.Label);
        Assert.Equal(ContactAtSeconds + play.HangTimeSeconds, j.T, 6); // 着地の瞬間(landAt)
    }

    [Fact]
    public void BuildBattedBall_ClearlyMissedFly_NoJudgement_EvenWhenDropped()
    {
        var play = DroppedFly(catchMargin: -2.0); // 完全に抜けた当たり＝フェアは出さない
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15, closeFlyMarginSeconds: 0.15);

        Assert.Empty(tl.Judgements);
    }

    [Fact]
    public void BuildBattedBall_RoutineCatch_StaysOut_NeverFair()
    {
        // 捕球アウトにも margin は付くが、判定は常に「アウト」のまま（「フェア」は落球のみ）。
        var play = FieldingPlayFixtures.CaughtFly(
                FieldPosition.CenterField, landingZ: 90, hangTimeSeconds: 3.0, apexHeightM: 20)
            with { CatchMarginSeconds = 0.05 };
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15, closeFlyMarginSeconds: 0.15);

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Out, j.Kind);
    }

    /// <summary>投球開始→接触までのリード[s]（TimelineBuilder の ContactAt と同値。private const のため複製）。</summary>
    private const double ContactAtSeconds = 0.85;

    // --- PlayTimelineAdapter: OutAtEnd/CloseCall はそのままPlaybackRunへ通る ---

    [Fact]
    public void PlayTimelineAdapter_PassesThrough_OutAtEndAndCloseCall()
    {
        var play = InfieldSingle(judgementMargin: 0.05);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15);

        var playback = PlayTimelineAdapter.ToPlaybackPlay(tl);
        var runner = Assert.Single(playback.Runners, r => r.Label == "打");
        var seg = Assert.Single(runner.Segs);
        Assert.False(seg.OutAtEnd);
        Assert.True(seg.CloseCall);
    }

    // --- BaserunningModel: 本塁クロスプレーのCloseCallはしきい値駆動（境界=coeff.CloseCallMarginSeconds） ---

    private static readonly BaserunningCoefficients LargeThreshold = new() { HomeSuccessBias = 0.6, CloseCallMarginSeconds = 1000.0 };
    private static readonly BaserunningCoefficients TinyThreshold = new() { HomeSuccessBias = 0.6, CloseCallMarginSeconds = 1e-9 };

    private static HomePlayContext HomeContext(BaserunningCoefficients c)
    {
        var situation = new HomePlaySituation(new Vector3D(0, 0, 70), 3.0, new Player { ArmStrength = 60 }.ToFielder().ThrowSpeedMps);
        return new HomePlayContext(Field, situation, new KokoSim.Engine.Match.Tactics.TacticsCoefficients(), 1.0,
            KokoSim.Engine.Match.Tactics.DefenseDepth.Normal, false);
    }

    [Fact]
    public void HomeDash_WithHugeThreshold_EveryContestedPlay_IsCloseCall()
    {
        var rng = new Xoshiro256Random(7);
        var found = 0;
        for (var i = 0; i < 200; i++)
        {
            var bases = new BaseState { Third = new Player { Speed = 50, Baserunning = 50 } };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, KokoSim.Engine.Match.AtBat.PlateAppearanceResult.Single, new Player(), 0,
                LargeThreshold, rng, collectMoves: true, HomeContext(LargeThreshold));
            foreach (var m in moves.Where(m => m.FromBase == 3 && m.ToBase == 4))
            {
                found++;
                Assert.True(m.CloseCall, "しきい値が極端に大きいのにCloseCallがfalse");
            }
        }
        Assert.True(found > 0, "本塁への移動が一度も記録されていない");
    }

    [Fact]
    public void HomeDash_WithTinyThreshold_NoPlay_IsCloseCall()
    {
        var rng = new Xoshiro256Random(7);
        var found = 0;
        for (var i = 0; i < 200; i++)
        {
            var bases = new BaseState { Third = new Player { Speed = 50, Baserunning = 50 } };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, KokoSim.Engine.Match.AtBat.PlateAppearanceResult.Single, new Player(), 0,
                TinyThreshold, rng, collectMoves: true, HomeContext(TinyThreshold));
            foreach (var m in moves.Where(m => m.FromBase == 3 && m.ToBase == 4))
            {
                found++;
                Assert.False(m.CloseCall, "しきい値がほぼ0なのにCloseCallがtrue（境界ちょうどでない限り）");
            }
        }
        Assert.True(found > 0, "本塁への移動が一度も記録されていない");
    }

    // --- 本塁打の判定オーバーレイ＋飛距離（issue #354） ---

    [Fact]
    public void BuildBattedBall_HomeRun_EmitsSingleHomeRunJudgement_AtLandingTime()
    {
        var play = FieldingPlayFixtures.HomeRun();
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.HomeRun, j.Kind);
        // 着地の瞬間＝結果確定時刻（本塁打の resolvedAt と同じ）に出す。
        Assert.Equal(tl.ResolvedAt, j.T, 6);
    }

    [Fact]
    public void BuildBattedBall_HomeRun_JudgementCarriesRangeM_AsDistance()
    {
        var play = FieldingPlayFixtures.HomeRun();
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

        // 載せる飛距離は判定に使った着地までの水平距離そのもの（表示専用・新たな計算をしない）。
        var j = Assert.Single(tl.Judgements);
        Assert.Equal(play.RangeM, j.DistanceM!.Value, 6);
    }

    [Fact]
    public void PlaybackAdapter_HomeRun_DistanceReachesJudgementAt()
    {
        var play = FieldingPlayFixtures.HomeRun();
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        var pb = PlayTimelineAdapter.ToPlaybackPlay(tl);

        var found = PlaybackEvaluator.JudgementAt(pb, tl.ResolvedAt, holdSeconds: 0.6);
        Assert.NotNull(found);
        Assert.Equal(JudgementKind.HomeRun, found!.Value.Kind);
        Assert.Equal(play.RangeM, found.Value.DistanceM!.Value, 6);
    }

    [Fact]
    public void BuildBattedBall_NonHomeRun_JudgementHasNoDistance()
    {
        // 既存の判定（アウト/セーフ/フェア）は飛距離を持たない＝描画側で詳細行が出ない。
        var play = InfieldOut(judgementMargin: -0.5);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 0.15);

        Assert.All(tl.Judgements, j => Assert.Null(j.DistanceM));
    }
}

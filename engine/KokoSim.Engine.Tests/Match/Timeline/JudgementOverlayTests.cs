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

    // --- TimelineBuilder.BuildBattedBall: CloseCall はしきい値駆動で、アウト表示自体には影響しない ---

    private static FieldingPlay InfieldSingle(double judgementMargin) => new()
    {
        Result = BattedBallResult.Single,
        LandingX = -9.0, LandingZ = 30.0, HangTimeSeconds = 0.3, ApexHeightM = 0.4,
        RangeM = 31.3, BearingDeg = -16.0, FielderRole = FieldPosition.Shortstop,
        FieldedAtSeconds = 1.4, ThrowArriveSeconds = 4.19, BatterToFirstSeconds = 4.2, IsFly = false,
        JudgementMarginSeconds = judgementMargin,
    };

    private static FieldingPlay InfieldOut(double judgementMargin) => new()
    {
        Result = BattedBallResult.Out,
        LandingX = -9.0, LandingZ = 30.0, HangTimeSeconds = 0.3, ApexHeightM = 0.4,
        RangeM = 31.3, BearingDeg = -16.0, FielderRole = FieldPosition.Shortstop,
        FieldedAtSeconds = 1.4, ThrowArriveSeconds = 3.0, BatterToFirstSeconds = 4.2, IsFly = false,
        JudgementMarginSeconds = judgementMargin,
    };

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
        var play = new FieldingPlay
        {
            Result = BattedBallResult.Out, LandingX = 0, LandingZ = 90, HangTimeSeconds = 3.0, ApexHeightM = 20,
            RangeM = 90, BearingDeg = 0, FielderRole = FieldPosition.CenterField,
            FieldedAtSeconds = 3.0, BatterToFirstSeconds = 4.2, IsFly = true,
        };
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play), closeCallMarginSeconds: 1000.0);

        Assert.False(BatterLeg(tl).CloseCall);
    }

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
}

using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// カメラ演出（design-06/design-12・Issue #119）のエンジン実出力（PlayTimelineAdapter経由）回帰テスト。
///
/// <see cref="CameraPlanBuilderTests"/> はビュー側の忠実移植モデル（<see cref="PlaybackSamples"/> の
/// <see cref="PitchSegment"/>/<see cref="ThrowSegment"/>）だけを検証しており、実出力
/// （<see cref="EndpointBallSegment"/>）で固定フレーム判定が成立しない不具合（Issue #258）を
/// 見逃していた。このファイルは実出力形のみを対象に同じ性質を再検証する。
/// </summary>
public sealed class CameraPlanBuilderRealOutputTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team BuildTeam(string name)
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
            Reserves = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    // 固定シードで試合を回し、投球区間を持つ打球プレー1つと、送球区間を持つゴロアウト1つを採取する。
    private static (PlayTimeline AnyBattedBall, PlayTimeline GroundOutWithThrow) CollectSamples()
    {
        PlayTimeline? any = null;
        PlayTimeline? groundOutWithThrow = null;
        for (var seed = 1UL; seed <= 400 && (any is null || groundOutWithThrow is null); seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log)
            {
                if (e.Timeline is null) continue;
                var tl = e.Timeline;
                if (any is null && tl.Ball.Any(b => b.Kind == BallSegmentKind.Pitch)) any = tl;
                if (groundOutWithThrow is null && e.Result == PlateAppearanceResult.InPlayOut
                    && tl.Ball.Any(b => b.Kind == BallSegmentKind.Throw))
                    groundOutWithThrow = tl;
            }
        }
        Assert.NotNull(any);
        Assert.NotNull(groundOutWithThrow);
        return (any!, groundOutWithThrow!);
    }

    [Fact]
    public void Build_KeepsFixedFrame_DuringPitchSegment_ForRealEngineOutput()
    {
        var (battedBall, _) = CollectSamples();
        var play = PlayTimelineAdapter.ToPlaybackPlay(battedBall);
        var pitchSeg = Assert.Single(play.Ball, b => b is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Pitch);

        var plan = CameraPlanBuilder.Build(play);

        // 区間全体（接触直前ぎりぎりまで＝先取りサンプリングでパンが始まっていた箇所を含む）で視野が不変。
        var first = CameraEvaluator.ViewportAt(plan, pitchSeg.T0);
        for (var t = pitchSeg.T0; t < pitchSeg.T1; t += 0.02)
        {
            var vp = CameraEvaluator.ViewportAt(plan, t);
            Assert.Equal(first.Cx, vp.Cx, 6);
            Assert.Equal(first.Cy, vp.Cy, 6);
            Assert.Equal(first.ZoomMult, vp.ZoomMult, 6);
        }
    }

    [Fact]
    public void Build_FramesThrowOriginAndDestination_ForRealEngineOutput()
    {
        var (_, groundOut) = CollectSamples();
        var play = PlayTimelineAdapter.ToPlaybackPlay(groundOut);
        var throwSeg = play.Ball.First(b => b is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Throw);

        var plan = CameraPlanBuilder.Build(play);
        var midT = throwSeg.T0 + (throwSeg.T1 - throwSeg.T0) / 2.0;
        var vp = CameraEvaluator.ViewportAt(plan, midT);

        var from = throwSeg.At(0);
        var to = throwSeg.At(throwSeg.T1 - throwSeg.T0);
        AssertWithinViewport(vp, from.X, from.Y);
        AssertWithinViewport(vp, to.X, to.Y);
    }

    // ---- issue #448①: 送球開始前（球は野手の手の中）にカメラが受け手側へ先行しない --------------

    /// <summary>
    /// 3D守備ビューの設定（<c>ThrowFollowsBall=true</c>）では、内野ゴロの一塁封殺は打者走者レッグが
    /// 接触直後から OutAtEnd=true で有効なので、critical（差し迫った走塁）分岐が野手の保持区間にも
    /// 及んでしまう。この分岐も leadT でボール位置を引いていたため、送球区間の未来位置を先取りして
    /// カメラがリリース前から一塁へ動き出していた（実測で確認・修正対象）。
    /// 捕球〜送球開始の間は視野中心が野手の保持位置に留まり、一塁側へにじり寄らないことを固定する。
    /// </summary>
    [Fact]
    public void Build_PinsToFielder_WhileBallHeldBeforeThrow_WithThrowFollowsBall()
    {
        var (_, groundOut) = CollectSamples();
        var play = PlayTimelineAdapter.ToPlaybackPlay(groundOut);
        var throwSeg = play.Ball.First(b => b is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Throw);

        var framing = new CameraFraming(40.0, 2.0, CameraFraming.Default.PitchZoom, ThrowFollowsBall: true);
        var plan = CameraPlanBuilder.Build(play, framing);

        // 送球開始のキーフレーム（T0）そのものは既に送球区間のフレーミングへ切り替わる（意図どおり）ので、
        // その一つ手前＝保持中最後のキーフレームで判定する（レート制限の収束余地を見込む）。
        var heldKf = plan.Last(k => k.T < throwSeg.T0 - 1e-9);
        Assert.True(!play.Ball.Any(b => b.T0 <= heldKf.T && heldKf.T <= b.T1),
            "判定に使うキーフレームがどのボールセグメントにも覆われていない（＝保持中）こと");
        var held = PlaybackEvaluator.BallAt(play, heldKf.T);
        Assert.True(held.HasValue);

        Assert.Equal(held!.Value.X, heldKf.Cx, 1);
        Assert.Equal(held.Value.Y, heldKf.Cy, 1);
    }

    private static void AssertWithinViewport(CameraViewport vp, double x, double y)
    {
        const double designWidthM = 760.0 / 3.35;
        const double designHeightM = 520.0 / 3.35;
        var halfW = designWidthM / 2.0 / vp.ZoomMult;
        var halfH = designHeightM / 2.0 / vp.ZoomMult;
        Assert.True(Math.Abs(x - vp.Cx) <= halfW + 1e-6,
            $"x={x:F2} outside viewport half-width {halfW:F2} around cx={vp.Cx:F2}");
        Assert.True(Math.Abs(y - vp.Cy) <= halfH + 1e-6,
            $"y={y:F2} outside viewport half-height {halfH:F2} around cy={vp.Cy:F2}");
    }
}

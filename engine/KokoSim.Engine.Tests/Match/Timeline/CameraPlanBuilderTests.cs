using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// タイムライン駆動カメラワーク（Issue #119）のキーフレーム生成／評価の単体テスト。
/// PlaybackSamples.All（7プレー）を素材に、完了条件で求められている性質
/// （被写体が視野に収まる・カット遷移の順序・速度上限）を純関数レベルで検証する。
/// </summary>
public sealed class CameraPlanBuilderTests
{
    // #208 回帰: ResAt=+∞（結果チップを出さないプレー＝PitchPlaybackFactory.PitchOnly）でも、
    // 生成ループが停止し有限個のキーフレームで返ること（以前は end=∞ で無限ループ→OOM だった）。
    [Fact]
    public void Build_Terminates_WhenResAtIsInfinite()
    {
        var play = PitchPlaybackFactory.PitchOnly(first: true, second: false, third: true);
        Assert.False(double.IsFinite(play.ResAt));   // 前提: 投球のみプレーは ResAt=+∞

        var plan = CameraPlanBuilder.Build(play);

        Assert.NotEmpty(plan);
        // 上限（MaxPlanSeconds=120s）÷ サンプル間隔（0.08s）＋端数を大きく超えないこと＝暴走していない。
        Assert.True(plan.Count < 2000, $"キーフレームが多すぎる（暴走の疑い）: {plan.Count}");
        Assert.All(plan, kf => Assert.True(double.IsFinite(kf.T)));
    }

    [Fact]
    public void Build_StartsOnPitchFraming_MoundToHome()
    {
        var play = PlaybackSamples.All[0]; // ① レフト前ヒット
        var plan = CameraPlanBuilder.Build(play);

        Assert.NotEmpty(plan);
        var first = plan[0];
        // 投手ー本塁の中点付近・全景よりズームインしていること。
        Assert.True(first.ZoomMult > CameraPlanBuilder.WideZoom);
        Assert.InRange(first.Cx, -1.0, 1.0);
        Assert.InRange(first.Cy, 0.0, FieldDiagramGeometry.Mound.Y);
    }

    [Fact]
    public void Build_ReturnsToWide_AfterResultSettles()
    {
        var play = PlaybackSamples.All[1]; // ② センターフライ（ResAt=4.47, Dur=5.6）
        var plan = CameraPlanBuilder.Build(play);

        var last = plan[^1];
        Assert.Equal(CameraPlanBuilder.WideZoom, last.ZoomMult, 3);
        Assert.Equal(CameraPlanBuilder.WideCx, last.Cx, 3);
        Assert.Equal(CameraPlanBuilder.WideCy, last.Cy, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Build_NeverExceedsZoomOrPanRateCaps(int playIndex)
    {
        var play = PlaybackSamples.All[playIndex];
        var plan = CameraPlanBuilder.Build(play);

        for (var i = 1; i < plan.Count; i++)
        {
            var prev = plan[i - 1];
            var next = plan[i];
            var dt = next.T - prev.T;
            if (dt <= 0) continue;

            var zoomRatio = next.ZoomMult / prev.ZoomMult;
            var maxRatio = Math.Pow(CameraPlanBuilder.ZoomRateCapPerSecond, dt) * 1.0001; // 浮動小数誤差の余裕
            Assert.True(zoomRatio <= maxRatio && zoomRatio >= 1.0 / maxRatio,
                $"play{playIndex} kf{i}: zoom {prev.ZoomMult:F3}->{next.ZoomMult:F3} exceeds cap over dt={dt:F3}");

            var dx = next.Cx - prev.Cx;
            var dy = next.Cy - prev.Cy;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            var maxDist = CameraPlanBuilder.PanSpeedCapMPerSecond * dt * 1.0001;
            Assert.True(dist <= maxDist,
                $"play{playIndex} kf{i}: pan distance {dist:F3}m exceeds cap over dt={dt:F3}");
        }
    }

    [Fact]
    public void Build_KeepsBallAndLookaheadWithinFramedBox_DuringFlight()
    {
        // ① レフト前ヒット。滞空/転がりの間、ボール現在地と0.6秒先読みの両方が視野内にあることを確認。
        var play = PlaybackSamples.All[0];
        var plan = CameraPlanBuilder.Build(play);

        for (var t = 0.9; t < play.ResAt; t += 0.1)
        {
            var vp = CameraEvaluator.ViewportAt(plan, t);
            var ball = PlaybackEvaluator.BallAt(play, t);
            if (!ball.HasValue) continue;
            var ahead = PlaybackEvaluator.BallAt(play, t + CameraPlanBuilder.LookaheadSeconds) ?? ball.Value;

            AssertWithinViewport(vp, ball.Value.X, ball.Value.Y, t, "ball");
            AssertWithinViewport(vp, ahead.X, ahead.Y, t, "lookahead");
        }
    }

    [Fact]
    public void Build_TracksBallAndTargetBase_DuringCriticalRunLeg()
    {
        // ⑥ 右中間破る長打→中継バックホーム。三塁から本塁への最終レッグは OutAtEnd=true（本塁憤死）。
        var play = PlaybackSamples.All[5];
        var criticalLeg = play.Runners[0].Segs.Last();
        Assert.True(criticalLeg.OutAtEnd);

        var plan = CameraPlanBuilder.Build(play);
        var midT = (criticalLeg.T0 + criticalLeg.T1) / 2.0;
        var vp = CameraEvaluator.ViewportAt(plan, midT);

        AssertWithinViewport(vp, FieldDiagramGeometry.Home.X, FieldDiagramGeometry.Home.Y, midT, "target base (home)");
    }

    [Fact]
    public void Build_FramesThrowOriginAndDestination()
    {
        // ③ ショートゴロ 6-3：Throw(2.55, from=(-7.5,35.8), to=(19.6,19.6), spd=32)
        var play = PlaybackSamples.All[2];
        var plan = CameraPlanBuilder.Build(play);
        var throwSeg = (ThrowSegment)play.Ball[2];
        var midT = throwSeg.T0 + (throwSeg.T1 - throwSeg.T0) / 2.0;
        var vp = CameraEvaluator.ViewportAt(plan, midT);

        AssertWithinViewport(vp, -7.5, 35.8, midT, "throw origin");
        AssertWithinViewport(vp, 19.6, 19.6, midT, "throw destination");
    }

    // 視野に入っているかを、engine 側の設計キャンバス基準（DesignW/H=760/520・S=3.35）で近似判定する。
    // 実際の描画は Unity 側の Scale(k) をそのまま掛けるだけなので、この基準がそのままスクリーン上の視野になる。
    private static void AssertWithinViewport(CameraViewport vp, double x, double y, double t, string label)
    {
        const double designWidthM = 760.0 / 3.35;
        const double designHeightM = 520.0 / 3.35;
        var halfW = designWidthM / 2.0 / vp.ZoomMult;
        var halfH = designHeightM / 2.0 / vp.ZoomMult;
        Assert.True(Math.Abs(x - vp.Cx) <= halfW + 1e-6,
            $"t={t:F2} {label} x={x:F2} outside viewport half-width {halfW:F2} around cx={vp.Cx:F2}");
        Assert.True(Math.Abs(y - vp.Cy) <= halfH + 1e-6,
            $"t={t:F2} {label} y={y:F2} outside viewport half-height {halfH:F2} around cy={vp.Cy:F2}");
    }
}

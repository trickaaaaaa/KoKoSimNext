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

    // 途中から組むプラン（issue #485/#490 の実機確認 2026-08-06）。打席途中の盗塁は投球を
    // 対決ビューで見せ終わってから守備ビューへ切り替わるので、カメラも投球区間の後ろから始める。
    // t=0 から組んだプランを途中から読むと、レート制限（ズーム 2倍/秒）の履歴を引きずって
    // 頭が全景のままになり、寄り切る前にプレーが終わる（実測: 頭の1.5秒が枠64.7m＝人18px）。
    [Fact]
    public void Build_WithStartSeconds_BeginsTightInsteadOfWide()
    {
        var play = PlaybackSamples.All[0];   // ① レフト前ヒット（頭に投球区間を持つ）
        const double start = 1.4;            // 投球が捕手へ届いたあたり＝盗塁の再生開始点に相当

        var whole = CameraPlanBuilder.Build(play);
        var sliced = CameraPlanBuilder.Build(play, null, start);

        Assert.NotEmpty(sliced);
        // ① 開始時刻より手前のキーフレームを持たない＝履歴を持ち込まない。
        Assert.All(sliced, kf => Assert.True(kf.T >= start - 1e-9, $"開始より手前のキーフレーム: {kf.T}"));
        // ② 開始時刻ちょうどに1本ある（等間隔グリッドが跨いだままにしない）。
        Assert.True(Math.Abs(sliced[0].T - start) < 1e-9, $"頭が開始時刻でない: {sliced[0].T}");
        // ③ 頭はレート制限を通さない生の目標値＝全景から始まらない。
        Assert.True(sliced[0].ZoomMult > CameraPlanBuilder.WideZoom,
            $"頭が全景のまま: zoom={sliced[0].ZoomMult}");
        // ④ 通しで組んだプランの同時刻より寄っている（＝これが直したかった差そのもの）。
        var wholeAtStart = whole.Last(kf => kf.T <= start + 1e-9);
        Assert.True(sliced[0].ZoomMult >= wholeAtStart.ZoomMult,
            $"途中開始のほうが引いている: {sliced[0].ZoomMult} < {wholeAtStart.ZoomMult}");
    }

    // 既定（startSeconds=0）は1キーフレームも変わらないこと＝既存の呼び出しとゴールデンが無傷。
    [Fact]
    public void Build_WithZeroStart_IsIdenticalToDefault()
    {
        foreach (var play in PlaybackSamples.All)
        {
            var a = CameraPlanBuilder.Build(play);
            var b = CameraPlanBuilder.Build(play, null, 0.0);
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].T, b[i].T);
                Assert.Equal(a[i].Cx, b[i].Cx);
                Assert.Equal(a[i].Cy, b[i].Cy);
                Assert.Equal(a[i].ZoomMult, b[i].ZoomMult);
            }
        }
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

    // ---- issue #406②: 全景復帰は ResAt でなく lastEvent（球が動き終わる／実際に動く走者レッグの
    // 終わり）基準 -----------------------------------------------------------------------------

    [Fact]
    public void LastEventTime_IgnoresStationaryRunnerLegs_ButCountsMovingLegsAndBallSegments()
    {
        var stay = new PlaybackVec(1, 1);
        var play = new PlaybackPlay
        {
            Name = "t", Dur = 10, Result = "x", ResAt = 2.0,
            Ball = new PlaybackBallSegment[] { new ThrowSegment(0.5, new PlaybackVec(0, 0), new PlaybackVec(0, 10), 20) },
            Runners = new[]
            {
                // 塁上で止まる走者（T1=9.0 が最大でも数えない＝AppendHeldRunners 相当）。
                new PlaybackRunner { Label = "待", Segs = new[] { new PlaybackRun(4.0, 9.0, stay, stay) } },
                // 動かない同一座標のレッグ。
                new PlaybackRunner { Label = "打", Segs = new[] { new PlaybackRun(0.0, 5.0, stay, stay) } },
                // 実際に動くレッグ（決着後まで続く＝犠飛のタッチアップ〜生還に相当）。
                new PlaybackRunner { Label = "走", Segs = new[] { new PlaybackRun(0.5, 3.5, new PlaybackVec(0, 0), new PlaybackVec(0, 27.43)) } },
            },
        };

        Assert.Equal(3.5, CameraPlanBuilder.LastEventTime(play), 6);
    }

    [Fact]
    public void Build_DelaysReturnToWide_WhenBallStillMovingPastResAt_ForSacFlyLikeThrow()
    {
        // 犠飛型の最小合成プレー: 決着(ResAt=捕球)は3.0sだが、バックホーム送球は5.0sまで続く。
        // 旧実装は play.ResAt+0.9=3.9s で無条件に全景へ戻していた＝タッチアップ〜生還の送球が
        // 見えない（issue #406②）。lastEvent（球が動き終わる時刻）基準なら、送球が終わるまで寄り続ける。
        var play = new PlaybackPlay
        {
            Name = "sac-fly-like", Dur = 6.0, Result = "F  犠飛", ResAt = 3.0,
            Ball = new PlaybackBallSegment[] { new ThrowSegment(3.0, new PlaybackVec(0, 90), new PlaybackVec(0, 0), 45) },
        };

        var plan = CameraPlanBuilder.Build(play);

        // 旧実装の戻し時刻（ResAt+0.9=3.9s）でも送球はまだ飛んでいる＝寄ったままのはず。
        var duringThrow = CameraEvaluator.ViewportAt(plan, play.ResAt + CameraPlanBuilder.ReturnToWideDelaySeconds + 0.1);
        Assert.True(duringThrow.ZoomMult > CameraPlanBuilder.WideZoom,
            $"送球中なのに全景へ戻っている: zoom={duringThrow.ZoomMult:F2}");

        // 送球が終わってから戻し遅延ぶん経てば、ちゃんと全景へ戻る（延長し続けない＝長打での過延長防止）。
        var afterThrow = CameraEvaluator.ViewportAt(plan, 5.0 + CameraPlanBuilder.ReturnToWideDelaySeconds + 2.5);
        Assert.Equal(CameraPlanBuilder.WideZoom, afterThrow.ZoomMult, 3);
    }

    // ---- issue #441: 死んだ球（全ボールセグメント終了後）のアップを続けない ------------------------

    [Fact]
    public void Build_ReturnsToWide_AfterBallDies_WhileRunnerStillCircling()
    {
        // 本塁打型の最小合成プレー: 打球は約3.1sで柵外に着弾して終わり、打者走者の一周は15.0sまで続く。
        // 旧実装は lastEvent（走者レッグ込み＝一周の終端）+0.9s まで「保持位置＝着弾点への最大ズーム」を
        // 目標にし続けた＝生還まで誰も居ない着弾点のアップが続きフリーズに見える（issue #441）。
        var landing = new PlaybackVec(30, 100);
        var play = new PlaybackPlay
        {
            Name = "hr-like", Dur = 16.0, Result = "本塁打", ResAt = 3.0,
            Ball = new PlaybackBallSegment[] { new ThrowSegment(0.5, new PlaybackVec(0, 0), landing, 40) },
            Runners = new[]
            {
                new PlaybackRunner
                {
                    Label = "打",
                    Segs = new[] { new PlaybackRun(0.6, 15.0, new PlaybackVec(0, 0), new PlaybackVec(0, 27.43)) },
                },
            },
        };
        var ballEnd = play.Ball[0].T1;
        Assert.True(CameraPlanBuilder.LastEventTime(play) >= 15.0);   // 前提: 走者レッグが lastEvent を引っ張る
        // 全景開始（プランと3Dカットの単一ソース）は着弾＋余韻へ前倒しされる。
        Assert.Equal(ballEnd + CameraPlanBuilder.DeadBallLingerSeconds, CameraPlanBuilder.WideStartTime(play), 6);

        var plan = CameraPlanBuilder.Build(play);

        // 着弾直後の余韻（テロップを読む間）はまだ全景に戻り切っていない＝即切り替えない。
        var linger = CameraEvaluator.ViewportAt(plan, ballEnd + CameraPlanBuilder.DeadBallLingerSeconds * 0.5);
        Assert.True(
            linger.ZoomMult > CameraPlanBuilder.WideZoom || Math.Abs(linger.Cy - CameraPlanBuilder.WideCy) > 1.0,
            $"余韻なしで即全景へ戻っている: zoom={linger.ZoomMult:F2} cy={linger.Cy:F1}");

        // 走者がまだ回っている中間時刻では、着弾点へのアップでなく全景に戻り切っている
        // （余韻＋レート制限の収束を見込み、着弾+5秒以降を見る）。
        foreach (var t in new[] { ballEnd + 5.0, 10.0, 14.0 })
        {
            var v = CameraEvaluator.ViewportAt(plan, t);
            Assert.Equal(CameraPlanBuilder.WideZoom, v.ZoomMult, 3);
            Assert.Equal(CameraPlanBuilder.WideCx, v.Cx, 3);
            Assert.Equal(CameraPlanBuilder.WideCy, v.Cy, 3);
        }
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

    // ---- フレーミングの差し替え（打球処理の3D化 F3） -------------------------
    //
    // 3Dの守備カメラは2Dの俯瞰より深く寄る必要があるので、寄りの深さだけを CameraFraming で
    // 差し替えられるようにした。演出の判断（何を追うか）とレート制限は共有したまま。

    /// <summary>
    /// framing を渡さない呼び出しは**1キーフレームも変わらない**。
    /// 既存の2D俯瞰の絵とゴールデンを守る担保で、ここが割れたら 3D 用の変更が2Dへ漏れている。
    /// </summary>
    [Fact]
    public void Build_WithoutFraming_IsIdenticalToDefaultFraming()
    {
        foreach (var play in PlaybackSamples.All)
        {
            var implicitPlan = CameraPlanBuilder.Build(play);
            var explicitPlan = CameraPlanBuilder.Build(play, CameraFraming.Default);

            Assert.Equal(implicitPlan.Count, explicitPlan.Count);
            for (var i = 0; i < implicitPlan.Count; i++)
                Assert.Equal(implicitPlan[i], explicitPlan[i]);
        }
    }

    /// <summary>
    /// ズーム上限を上げると、上限で頭打ちになっていた区間が <c>FitBox</c> の要求値まで寄る。
    /// 外野への打球（③ センターフライ）は現行 MaxZoom=3.4 に張り付いており、
    /// 3Dではここが「野手が十数pxにしかならない」の原因になっていた。
    /// </summary>
    [Fact]
    public void Build_WithHigherMaxZoom_ZoomsPastTheDefaultCap()
    {
        var play = PlaybackSamples.All.First(p => p.Name.Contains("フライ"));

        var tight = new CameraFraming(MaxZoom: 8.0, MarginM: CameraFraming.Default.MarginM,
            PitchZoom: CameraFraming.Default.PitchZoom);
        var basePlan = CameraPlanBuilder.Build(play);
        var tightPlan = CameraPlanBuilder.Build(play, tight);

        var baseMax = basePlan.Max(k => k.ZoomMult);
        var tightMax = tightPlan.Max(k => k.ZoomMult);

        // 既定は上限に張り付いている（＝要求はもっと上だった）
        Assert.Equal(CameraFraming.Default.MaxZoom, baseMax, 3);
        // 上限を上げると、その上限までの間で実際に深く寄る
        Assert.True(tightMax > baseMax + 0.5,
            $"上限を上げてもズームが伸びていない: base={baseMax:F2} tight={tightMax:F2}");
        Assert.True(tightMax <= tight.MaxZoom + 1e-6, $"新しい上限を超えている: {tightMax:F2}");
    }

    /// <summary>
    /// 上限を上げても**レート制限は効いたまま**（酔い防止は寄りの深さと独立）。
    /// </summary>
    [Fact]
    public void Build_WithHigherMaxZoom_StillRespectsZoomRateCap()
    {
        var play = PlaybackSamples.All.First(p => p.Name.Contains("フライ"));
        var plan = CameraPlanBuilder.Build(play, new CameraFraming(8.0, 6.0, 2.4));

        for (var i = 1; i < plan.Count; i++)
        {
            var dt = plan[i].T - plan[i - 1].T;
            if (dt <= 0) continue;
            var ratio = plan[i].ZoomMult / plan[i - 1].ZoomMult;
            var cap = Math.Pow(CameraPlanBuilder.ZoomRateCapPerSecond, dt);
            Assert.True(ratio <= cap + 1e-9 && ratio >= 1.0 / cap - 1e-9,
                $"t={plan[i].T:F2} でズーム変化率が上限を超えた: ratio={ratio:F4} cap={cap:F4}");
        }
    }

    /// <summary>
    /// 送球区間の枠の取り方（<see cref="CameraFraming.ThrowFollowsBall"/>）。
    /// 既定＝始終点を両方収める＝**送球の距離ぶん必ず引く**。3Dの守備カメラはこれを切って球を追う。
    /// 6-3（遊撃→一塁の約30m）で差が出ることを固定する。
    /// </summary>
    [Fact]
    public void Build_WithThrowFollowsBall_ZoomsTighterDuringTheThrow()
    {
        var play = PlaybackSamples.All[2]; // ③ ショートゴロ 6-3
        var seg = play.Ball.First(s =>
            s is ThrowSegment
            || (s is EndpointBallSegment e && e.Kind == Engine.Match.Timeline.BallSegmentKind.Throw));

        var framing = new CameraFraming(MaxZoom: 40.0, MarginM: 2.0, PitchZoom: 2.4);
        var spanPlan = CameraPlanBuilder.Build(play, framing);
        var followPlan = CameraPlanBuilder.Build(play, framing with { ThrowFollowsBall = true });

        static double MaxZoomIn(System.Collections.Generic.IReadOnlyList<CameraKeyframe> plan, double t0, double t1)
            => plan.Where(k => k.T >= t0 && k.T <= t1).Max(k => k.ZoomMult);

        // 送球中〜到達直後（受け手が捕るところまで）で比べる。
        var spanMax = MaxZoomIn(spanPlan, seg.T0, seg.T1 + 0.3);
        var followMax = MaxZoomIn(followPlan, seg.T0, seg.T1 + 0.3);

        Assert.True(followMax > spanMax + 0.5,
            $"球を追ってもズームが伸びていない: span={spanMax:F2} follow={followMax:F2}");
        // 上限とレート制限は共通の仕組みなので、切り替えても効いたまま。
        Assert.True(followMax <= framing.MaxZoom + 1e-6, $"上限を超えている: {followMax:F2}");
    }

    /// <summary>既定（2D俯瞰）は <c>ThrowFollowsBall=false</c> ＝従来の始終点フレームのまま。</summary>
    [Fact]
    public void Default_DoesNotFollowBallDuringThrow()
        => Assert.False(CameraFraming.Default.ThrowFollowsBall);

    // ---- issue #448①: 送球開始前（球は野手の手の中）にカメラが受け手側へ先行しない --------------

    /// <summary>
    /// ③ ショートゴロ 6-3。ゴロの捕球（Roll終端 T1=2.18）〜送球開始（Throw T0=2.55）の間は
    /// 球が野手の手の中にある。旧実装は leadT(+0.25s)/lookahead(+0.6s) でボール位置を先取りしていたため、
    /// この保持中の後半で既に送球経路上（一塁寄り）の未来位置を目標にしてしまっていた。
    /// 保持中は視野中心が捕球位置（ショートの位置）に留まることを固定する。
    /// </summary>
    [Fact]
    public void Build_PinsToFielder_WhileBallHeldBeforeThrow()
    {
        var play = PlaybackSamples.All[2]; // ③ ショートゴロ 6-3
        var rollSeg = play.Ball[1];
        var throwSeg = play.Ball[2];
        Assert.True(throwSeg.T0 - rollSeg.T1 > 0.1, "保持中の間が短すぎて検証にならない");

        var fieldedAt = rollSeg.At(rollSeg.T1 - rollSeg.T0);
        var plan = CameraPlanBuilder.Build(play);

        // 送球開始のキーフレーム（T0）そのものは既に送球区間のフレーミングへ切り替わる（意図どおり）ので、
        // その一つ手前＝保持中最後のキーフレームで判定する（レート制限の収束余地を見込む）。
        var heldKf = plan.Last(k => k.T < throwSeg.T0 - 1e-9);
        Assert.Equal(fieldedAt.X, heldKf.Cx, 1);
        Assert.Equal(fieldedAt.Y, heldKf.Cy, 1);
    }

    /// <summary>同じガードが <c>ThrowFollowsBall=true</c>（3D守備ビュー）でも効くことを固定する。</summary>
    [Fact]
    public void Build_PinsToFielder_WhileBallHeldBeforeThrow_WithThrowFollowsBall()
    {
        var play = PlaybackSamples.All[2]; // ③ ショートゴロ 6-3
        var rollSeg = play.Ball[1];
        var throwSeg = play.Ball[2];

        var fieldedAt = rollSeg.At(rollSeg.T1 - rollSeg.T0);
        var framing = new CameraFraming(40.0, 2.0, CameraFraming.Default.PitchZoom, ThrowFollowsBall: true);
        var plan = CameraPlanBuilder.Build(play, framing);

        var heldKf = plan.Last(k => k.T < throwSeg.T0 - 1e-9);
        Assert.Equal(fieldedAt.X, heldKf.Cx, 1);
        Assert.Equal(fieldedAt.Y, heldKf.Cy, 1);
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

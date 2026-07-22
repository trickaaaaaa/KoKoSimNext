using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

public sealed class AtBatPipelineTests
{
    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    // --- BattedBall 物理 ---

    [Fact]
    public void BattedBall_InitialVelocityMagnitudeEqualsExitVelocity()
    {
        var ball = new BattedBall { ExitVelocityMps = 40, LaunchAngleDeg = 20, BearingDeg = 15 };
        Assert.Equal(40.0, ball.InitialVelocity().Length, 6);
    }

    // --- 守備解決 ---

    [Fact]
    public void FieldingResolver_DeepDrive_IsHomeRun()
    {
        // 175km/h・28°・センター方向 → フェンス越え。
        var ball = new BattedBall { ExitVelocityMps = 175.0 / 3.6, LaunchAngleDeg = 28, BearingDeg = 0 };
        var res = FieldingResolver.Resolve(ball, Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(), new FieldingCoefficients(), new Xoshiro256Random(1));
        Assert.Equal(BattedBallResult.HomeRun, res);
    }

    [Fact]
    public void FieldingResolver_FarOutsideFairArc_IsFoul()
    {
        var ball = new BattedBall { ExitVelocityMps = 30, LaunchAngleDeg = 20, BearingDeg = 60 };
        var res = FieldingResolver.Resolve(ball, Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(), new FieldingCoefficients(), new Xoshiro256Random(1));
        Assert.Equal(BattedBallResult.Foul, res);
    }

    [Fact]
    public void FieldingResolver_CanOfCornFly_IsCaughtOut()
    {
        // 浅いフライ（95km/h・45°）→ 守備範囲内で捕球。エラー確率0で決定化。
        var ball = new BattedBall { ExitVelocityMps = 95.0 / 3.6, LaunchAngleDeg = 45, BearingDeg = 0 };
        var res = FieldingResolver.Resolve(ball, Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(), new FieldingCoefficients { ErrorBaseProb = 0 }, new Xoshiro256Random(1));
        Assert.Equal(BattedBallResult.Out, res);
    }

    [Fact]
    public void FieldingResolver_HardGrounder_IsNotHomeRunOrFoul()
    {
        var ball = new BattedBall { ExitVelocityMps = 32, LaunchAngleDeg = -3, BearingDeg = 5 };
        var res = FieldingResolver.Resolve(ball, Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(), new FieldingCoefficients(), new Xoshiro256Random(3));
        Assert.NotEqual(BattedBallResult.HomeRun, res);
        Assert.NotEqual(BattedBallResult.Foul, res);
    }

    // --- トランスファー(捕球→送球の握り替え)の守備(Fielding)紐づけ（Issue #36, design-02 §1.2）---

    [Fact]
    public void FieldingResolver_InfieldGrounder_AtFieldingFifty_TransferSlopeHasNoEffect()
    {
        // 守備50はトランスファー傾きの起点＝どんな傾き値でも影響が0（帯不変, 不変条件#5）。
        var ball = new BattedBall { ExitVelocityMps = 32, LaunchAngleDeg = -3, BearingDeg = 5 };
        var fielders = Field.StandardAlignment(new FielderAttributes { Fielding = 50 });
        var withSlope = FieldingResolver.ResolveDetailed(ball, Field, Aero, BatterAttributes.LeagueAverage,
            fielders, new FieldingCoefficients(), new Xoshiro256Random(3));
        var withoutSlope = FieldingResolver.ResolveDetailed(ball, Field, Aero, BatterAttributes.LeagueAverage,
            fielders, new FieldingCoefficients { TransferFieldingSlope = 0 }, new Xoshiro256Random(3));
        Assert.NotNull(withSlope.ThrowArriveSeconds);
        Assert.Equal(withSlope.ThrowArriveSeconds, withoutSlope.ThrowArriveSeconds);
    }

    [Fact]
    public void FieldingResolver_InfieldGrounder_HigherInfielderFielding_ThrowArrivesSooner()
    {
        var ball = new BattedBall { ExitVelocityMps = 32, LaunchAngleDeg = -3, BearingDeg = 5 };
        var coeff = new FieldingCoefficients();
        var slow = FieldingResolver.ResolveDetailed(ball, Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = 20 }), coeff, new Xoshiro256Random(3));
        var fast = FieldingResolver.ResolveDetailed(ball, Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = 90 }), coeff, new Xoshiro256Random(3));
        Assert.NotNull(slow.ThrowArriveSeconds);
        Assert.NotNull(fast.ThrowArriveSeconds);
        Assert.True(fast.ThrowArriveSeconds < slow.ThrowArriveSeconds,
            $"守備が高いほど送球到達が早くなっていない: slow={slow.ThrowArriveSeconds} fast={fast.ThrowArriveSeconds}");
    }

    // --- 打席解決 ---

    [Fact]
    public void AtBatResolver_IsDeterministic()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var batter = BatterAttributes.LeagueAverage;
        var pitcher = PitcherAttributes.LeagueAverage;

        for (ulong seed = 0; seed < 50; seed++)
        {
            var a = AtBatResolver.Resolve(batter, pitcher, ctx, new Xoshiro256Random(seed));
            var b = AtBatResolver.Resolve(batter, pitcher, ctx, new Xoshiro256Random(seed));
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void AtBatResolver_TerminatesWithValidOutcome()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var rng = new Xoshiro256Random(123);
        for (var i = 0; i < 500; i++)
        {
            var r = AtBatResolver.Resolve(
                BatterAttributes.LeagueAverage, PitcherAttributes.LeagueAverage, ctx, rng.Fork((ulong)i));
            Assert.True(Enum.IsDefined(r));
        }
    }

    [Fact]
    public void EliteContactHitter_StrikesOutLessThanWeakHitter()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var pitcher = PitcherAttributes.LeagueAverage;
        var elite = new BatterAttributes { Contact = 90, Discipline = 70 };
        var weak = new BatterAttributes { Contact = 25, Discipline = 35 };

        var root = new Xoshiro256Random(2024);
        int eliteK = 0, weakK = 0;
        for (var i = 0; i < 3000; i++)
        {
            if (AtBatResolver.Resolve(elite, pitcher, ctx, root.Fork((ulong)i)) == PlateAppearanceResult.Strikeout)
                eliteK++;
            if (AtBatResolver.Resolve(weak, pitcher, ctx, root.Fork((ulong)(i + 1_000_000))) == PlateAppearanceResult.Strikeout)
                weakK++;
        }
        Assert.True(eliteK < weakK, $"ミート高={eliteK}, ミート低={weakK}");
    }

    // --- F1: FieldingPlay 幾何の持ち回り（設計書12 §2.2） ---

    [Fact]
    public void Play_ExposedOnInPlay_WhenCaptureOn_AndConsistentWithTimeline()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), CaptureTimeline = true };
        var batter = new BatterAttributes { Contact = 60 };
        var pitcher = PitcherAttributes.LeagueAverage;

        // インプレー打球が出るまでシードを回す（三振/四球は Play=null なのでスキップ）。
        var root = new Xoshiro256Random(100);
        AtBatResult? inPlay = null;
        for (ulong s = 0; s < 200 && inPlay is null; s++)
        {
            var res = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, root.Fork(s));
            if (res.Result is not (PlateAppearanceResult.Strikeout or PlateAppearanceResult.Walk))
                inPlay = res;
        }

        Assert.NotNull(inPlay);
        Assert.NotNull(inPlay!.Play);        // インプレーは幾何を持つ
        Assert.NotNull(inPlay.Timeline);
        // 本塁打・ファウル以外は処理野手が確定し、タイムラインの結果表示と整合。
        if (inPlay.Result is not PlateAppearanceResult.HomeRun)
            Assert.Equal(inPlay.Timeline!.Result, TimelineBuilder.DescribeResult(inPlay.Play!));
    }

    [Fact]
    public void Play_IsNull_ForStrikeoutAndWalk()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), CaptureTimeline = true };
        var pitcher = PitcherAttributes.LeagueAverage;
        var root = new Xoshiro256Random(7);
        int checkedK = 0, checkedBB = 0;
        for (ulong s = 0; s < 500 && (checkedK == 0 || checkedBB == 0); s++)
        {
            // 三振しやすい打者と四球しやすい打者を別々に回す。
            var kRes = AtBatResolver.ResolveDetailed(new BatterAttributes { Contact = 15 }, pitcher, ctx, root.Fork(s));
            if (kRes.Result == PlateAppearanceResult.Strikeout) { Assert.Null(kRes.Play); checkedK++; }
            var bbRes = AtBatResolver.ResolveDetailed(new BatterAttributes { Discipline = 95 }, pitcher, ctx, root.Fork(s + 900_000));
            if (bbRes.Result == PlateAppearanceResult.Walk) { Assert.Null(bbRes.Play); checkedBB++; }
        }
        Assert.True(checkedK > 0 && checkedBB > 0);
    }

    [Fact]
    public void Play_IsPopulated_EvenWhenCaptureOff_ForInPlay_ButTimelineIsNot()
    {
        // F2: 本塁クロスプレーが結果に効くため Play(幾何) は捕捉オフでも常時保持する。
        // 一方タイムライン(表示専用)は従来通り捕捉オフでは作らない（統計シムは描画ゼロコストのまま）。
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), CaptureTimeline = false };
        var pitcher = PitcherAttributes.LeagueAverage;
        var root = new Xoshiro256Random(3);
        var checkedInPlay = 0;
        for (ulong s = 0; s < 100; s++)
        {
            var res = AtBatResolver.ResolveDetailed(new BatterAttributes { Contact = 60 }, pitcher, ctx, root.Fork(s));
            Assert.Null(res.Timeline); // 表示専用タイムラインは常に未構築
            if (res.Result is PlateAppearanceResult.Strikeout or PlateAppearanceResult.Walk)
                Assert.Null(res.Play);
            else { Assert.NotNull(res.Play); checkedInPlay++; }
        }
        Assert.True(checkedInPlay > 0);
    }

    // --- 結果分類ヘルパ ---

    [Theory]
    [InlineData(PlateAppearanceResult.Single, true, true, 1)]
    [InlineData(PlateAppearanceResult.HomeRun, true, true, 4)]
    [InlineData(PlateAppearanceResult.Walk, false, false, 0)]
    [InlineData(PlateAppearanceResult.Strikeout, false, true, 0)]
    [InlineData(PlateAppearanceResult.InPlayOut, false, true, 0)]
    public void ResultClassification(PlateAppearanceResult r, bool isHit, bool isAtBat, int bases)
    {
        Assert.Equal(isHit, r.IsHit());
        Assert.Equal(isAtBat, r.IsAtBat());
        Assert.Equal(bases, r.TotalBases());
    }
}

using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 守備のミスでもアウトになる打席の絵（issue #815・表示専用）。
/// ・弾いたがアウト: お手玉級のもたつき＋一塁アウトはお手玉の絵。内野安打（セーフ）は握り直しまで
/// ・悪送球だがアウト: 回収レースで拾い直しが間に合った打席は「逸れた送球→拾い直し→アウト」を描き、
///   アウトの時刻は判定が決めた時刻（defenseTime + 回収ロス）に揃う。「エラー」は出さない
/// ・判定結果と乱数消費は変えない
/// </summary>
public sealed class MisplayOutPictureTests
{
    private static readonly FieldGeometry Field = new();

    // ===== 弾いたがアウト =====

    private static EndpointBallSegment Seg(BallSegmentKind kind, double t0, double t1, double fx, double fz, double tx, double tz,
        FieldPosition? from = null, FieldPosition? to = null)
        => EndpointBallSegment.From(new BallSegment
        {
            Kind = kind, T0 = t0, T1 = t1, From = new TimelinePoint(fx, fz), To = new TimelinePoint(tx, tz),
            FromRole = from, ToRole = to, ToBase = to == FieldPosition.FirstBase ? "first" : null,
        });

    private static PlaybackPlay Grounder(double catchT, double releaseT, JudgementKind? batterCall) => new()
    {
        Name = "t", Dur = releaseT + 3, Result = "t", ResAt = releaseT + 1,
        Ball = new PlaybackBallSegment[]
        {
            Seg(BallSegmentKind.Pitch, 0.4, 0.85, 0, 18.44, 0, 0),
            Seg(BallSegmentKind.Bounce, 0.85, catchT, 0, 0, -13.47, 23.86),
            Seg(BallSegmentKind.Throw, releaseT, releaseT + 1.03, -13.47, 23.86, 19.4, 19.4,
                FieldPosition.ThirdBase, FieldPosition.FirstBase),
        },
        Judgements = batterCall is { } k
            ? new[] { new PlaybackJudgement(releaseT + 1.03, k, k == JudgementKind.Safe, "打") }
            : Array.Empty<PlaybackJudgement>(),
    };

    [Fact]
    public void LongFumbleThenOut_ShowsTheFumble()
        => Assert.Equal(HandleBobbleKind.Fumble,
            HandleBobblePlanner.Plan(Grounder(2.13, 2.13 + 2.15, JudgementKind.Out)).Kind);

    [Fact]
    public void LongFumbleThenSafe_StaysAJuggle()
    {
        Assert.Equal(HandleBobbleKind.Juggle,
            HandleBobblePlanner.Plan(Grounder(2.13, 2.13 + 2.15, JudgementKind.Safe)).Kind);
        Assert.Equal(HandleBobbleKind.Juggle,
            HandleBobblePlanner.Plan(Grounder(2.13, 2.13 + 2.15, null)).Kind);
    }

    [Fact]
    public void ShortHoldThenOut_IsNotAFumble()
        => Assert.Equal(HandleBobbleKind.Juggle,
            HandleBobblePlanner.Plan(Grounder(2.13, 2.13 + 1.8, JudgementKind.Out)).Kind);

    // ===== 悪送球だがアウト =====

    private static PlayTimeline Build(FieldingPlay play)
        => TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static FieldingPlay RecoveredThrow(double scatterM, bool byBackup, double outSeconds, double batterToFirst)
        => FieldingPlayFixtures.InfieldGrounder(
                FieldPosition.Shortstop, fieldedX: -9.0, fieldedZ: 30.0,
                fieldedAtSeconds: 1.4, throwArriveSeconds: 3.0, batterToFirstSeconds: batterToFirst,
                result: BattedBallResult.Out)
            with { RecoveredThrowScatterM = scatterM, RecoveredThrowByBackup = byBackup, RecoveredThrowOutSeconds = outSeconds };

    [Fact]
    public void RecoveredByFirstBaseman_ThrowMissesTheBag_FirstBasemanPicksItUp_OutAtTheVerdictTime()
    {
        var play = RecoveredThrow(scatterM: 3.0, byBackup: false, outSeconds: 3.8, batterToFirst: 4.6);
        var tl = Build(play);
        var first = new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z);
        var fieldPoint = new TimelinePoint(-9.0, 30.0);

        // 送球は塁に正着せず、送球方向へ逸れ幅ぶん先で止まる（受け手なし）
        var thrown = Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw);
        Assert.Null(thrown.ToBase);
        Assert.Null(thrown.ToRole);
        Assert.Equal(3.0, Dist(thrown.To, first), 3);
        Assert.Equal(Dist(fieldPoint, first) + 3.0, Dist(fieldPoint, thrown.To), 3);

        // 一塁手が逸れた球へ走り、アウトの時刻に拾う
        var outAt = TimelineBuilder.ContactAt + 3.8;
        var pick = Assert.Single(tl.Moves, m => m.Task == FielderTask.Chase);
        Assert.Equal(FieldPosition.FirstBase, pick.Role);
        Assert.Equal(outAt, pick.T1, 6);
        Assert.Equal(0.0, Dist(pick.To, thrown.To), 6);

        // 判定はアウト（エラーは出さない）・時刻は判定が決めた時刻
        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Out, j.Kind);
        Assert.Equal(outAt, j.T, 6);
        Assert.Equal(outAt, tl.ResolvedAt, 6);
    }

    [Fact]
    public void RecoveredByBackup_RightFielderPicksUpAndThrowsToFirst_ArrivalIsTheOut()
    {
        var play = RecoveredThrow(scatterM: 14.0, byBackup: true, outSeconds: 5.2, batterToFirst: 6.0);
        var tl = Build(play);
        var outAt = TimelineBuilder.ContactAt + 5.2;

        var throws = tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw).ToList();
        Assert.Equal(2, throws.Count);
        var errant = throws[0];
        var relay = throws[1];
        Assert.Null(errant.ToRole);
        Assert.Equal(FieldPosition.RightField, relay.FromRole);
        Assert.Equal("first", relay.ToBase);
        Assert.Equal(0.0, Dist(relay.From, errant.To), 6);
        Assert.Equal(outAt, relay.T1, 6);

        // 右翼手が逸れた球の所へ走る（バックアップの回収は標準配置ではほぼ起きない＝逸れ幅50m級が要る。
        // 実測 2026-11-08: 回収でアウトに戻った 10,187 打席中 0 件。ここでは絵の組み立てだけを確かめる）
        var pick = Assert.Single(tl.Moves, m => m.Task == FielderTask.Chase);
        Assert.Equal(FieldPosition.RightField, pick.Role);
        Assert.Equal(0.0, Dist(pick.To, errant.To), 6);
        Assert.True(pick.T0 < outAt, "決着後に走り出している（プレー後の定位置戻りを起点に取っている）");

        var j = Assert.Single(tl.Judgements);
        Assert.Equal(JudgementKind.Out, j.Kind);
        Assert.Equal(outAt, j.T, 6);
    }

    /// <summary>走者がいる打席は従来の絵（一塁へ正着する送球）。併殺・封殺の組み直しが最初の送球を差し替えるため。</summary>
    [Fact]
    public void RecoveredThrowWithRunnersOn_KeepsThePlainOutPicture()
    {
        var play = RecoveredThrow(scatterM: 3.0, byBackup: false, outSeconds: 3.8, batterToFirst: 4.6);
        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true, TimelineBuilder.DescribeResult(play));
        Assert.Single(tl.Ball, b => b.Kind == BallSegmentKind.Throw && b.ToBase == "first");
        Assert.DoesNotContain(tl.Moves, m => m.Task == FielderTask.Chase);
    }

    /// <summary>
    /// 実際の守備解決で、回収レースでアウトに戻った打席が値を持つ（結果はアウトのまま・失策は付かない）。
    /// 悪送球ロールを必ず当て、逸れ幅を小さくして拾い直しが間に合う打席を作る。
    /// </summary>
    [Fact]
    public void Resolver_RecordsTheRecoveryRace_OnlyWhenTheOutIsSaved()
    {
        var coeff = new FieldingCoefficients { ThrowErrorBaseProb = 1.0, ErrorMaxProb = 1.0, WildThrowScatterSigmaM = 2.0 };
        var recovered = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var p = Resolve(coeff, seed);
            if (p.RecoveredThrowScatterM is { } sc)
            {
                recovered++;
                Assert.Equal(BattedBallResult.Out, p.Result);
                Assert.Null(p.ErrorKind);
                Assert.True(sc >= 0.0);
                Assert.True(p.RecoveredThrowOutSeconds > p.ThrowArriveSeconds,
                    "拾い直しのアウトは正着の送球より遅い");
                Assert.True(p.RecoveredThrowOutSeconds + coeff.ForceOutMarginSeconds <= p.BatterToFirstSeconds + 1e-9,
                    "アウトの判定基準（守備＋余裕≦走者）を満たしていない");
            }
            else
            {
                Assert.Null(p.RecoveredThrowOutSeconds);
                Assert.False(p.RecoveredThrowByBackup);
            }
        }
        Assert.True(recovered > 0, "拾い直しのアウトが1件も出なかった（前提が崩れている）");
    }

    /// <summary>結果と乱数消費は変わらない: 送球エラーを切った係数では値が一切入らない（従来の経路のまま）。</summary>
    [Fact]
    public void Resolver_NoThrowErrors_NoRecoveryValues()
    {
        var coeff = new FieldingCoefficients { ThrowErrorBaseProb = 0.0 };
        for (ulong seed = 1; seed <= 200; seed++)
            Assert.Null(Resolve(coeff, seed).RecoveredThrowScatterM);
    }

    private static FieldingPlay Resolve(FieldingCoefficients coeff, ulong seed)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 34.0, LaunchAngleDeg = -4.0, BearingDeg = -12.0 },
            Field, new Aerodynamics(), BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { ThrowAccuracy = 50 }),
            coeff, new Xoshiro256Random(seed));
}

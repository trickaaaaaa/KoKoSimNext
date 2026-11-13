using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 送球の逸れを判定と絵の両方へ通す（issue #859）。送球1本につき逸れは1つ＝判定（遅れ・悪送球）に使った値が
/// そのまま絵の送球に載ること、判定の無い送球にも絵だけの逸れが載ること、絵を作っても試合結果は変わらないこと。
/// </summary>
public class ThrowScatterJudgedPictureTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    private static GameResult Play(ulong seed, bool capture)
    {
        var away = StrengthTeamFactory.Create(55, "先攻", new Xoshiro256Random(seed * 2));
        var home = StrengthTeamFactory.Create(55, "後攻", new Xoshiro256Random(seed * 2 + 1));
        return GameEngine.Play(away, home, new GameContext { CaptureTimelines = capture }, new Xoshiro256Random(seed));
    }

    [Fact]
    public void Race_ReturnsTheScatterItDrew()
    {
        // 勝負の最初に引くのは送球の逸れ（横・縦の正規2つ）＝同じシードの素の抽選と一致する。
        var s = new HomePlaySituation(new Vector3D(0, 0, 60), 3.0, 30.0, 50, ThrowerThrowAccuracy: 50);
        var race = HomePlayResolver.ResolveRace(
            new Player(), 2, s, Field, C, HomePlayResolver.HomeParams(C), new Xoshiro256Random(7));
        var expected = ThrowScatterModel.Sample(ThrowScatterModel.SigmaCmForAccuracy(50), new Xoshiro256Random(7));
        Assert.Equal(expected, race.Scatter);
    }

    [Fact]
    public void SampledDelay_IsZeroMean_SoTheReadAndTheVerdictAgree()
    {
        // 送り判断は期待値で読み、勝負はその本の遅れで引く。両者の平均が一致する（ゆらぎの平均は0）。
        foreach (var acc in new[] { 20, 50, 80 })
        {
            var rng = new Xoshiro256Random(11);
            var sum = 0.0;
            const int n = 200_000;
            for (var i = 0; i < n; i++) sum += HomePlayResolver.SampleThrowScatter(acc, C, rng).NoiseSeconds;
            Assert.InRange(sum / n, -0.001, 0.001);
        }
    }

    [Fact]
    public void WildThrowVerdict_UsesTheSameThrowAsTheRace()
    {
        // 送球は1本＝悪送球かどうかも、勝負に使った同じ逸れで決まる。逸れが閾値を超えなければ悪送球は起きない。
        var c = C with { RelayThrowErrorBaseProb = 0.05 };
        var thresholdCm = ThrowScatterModel.WildThresholdCmForProbability(ThrowScatterModel.SigmaCmForAccuracy(50), 0.05);
        var s = new HomePlaySituation(new Vector3D(0, 0, 40), 2.0, 30.0, 50, ThrowerThrowAccuracy: 50);
        var seen = 0;
        for (ulong seed = 1; seed <= 3000; seed++)
        {
            var (outcome, _, scatter) = HomePlayResolver.ResolveWithWildThrowVerdict(
                new Player(), 2, s, Field, c, HomePlayResolver.HomeParams(c), new Xoshiro256Random(seed));
            Assert.NotNull(scatter);
            if (outcome == HomePlayResolver.HomeThrowOutcome.SafeOnWildThrow)
            {
                seen++;
                Assert.True(scatter!.Value.RadialCm >= thresholdCm, $"閾値未満の逸れで悪送球: {scatter.Value.RadialCm:F1}cm");
            }
        }
        Assert.True(seen > 0);
    }

    [Fact]
    public void CapturingThePicture_DoesNotChangeTheGame()
    {
        // 絵だけの逸れは試合の乱数を進めない別の流れから引く＝絵を作っても作らなくても結果は同じ。
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var off = Play(seed, capture: false);
            var on = Play(seed, capture: true);
            Assert.Equal(GameResultDigest.Sha256Of(off), GameResultDigest.Sha256Of(on));
        }
    }

    [Fact]
    public void EveryThrowInThePicture_CarriesAScatter()
    {
        var throws = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            foreach (var e in Play(seed, capture: true).Log)
            {
                if (e.Timeline is not { } tl) continue;
                foreach (var seg in tl.Ball.Where(b => b.Kind == BallSegmentKind.Throw))
                {
                    if (seg.MissedBy is not null) { Assert.Null(seg.ThrowScatter); continue; }
                    throws++;
                    Assert.NotNull(seg.ThrowScatter);
                }
            }
        }
        Assert.True(throws > 100, $"送球が少なすぎる: {throws}");
    }

    [Fact]
    public void JudgedScatter_GoesToTheThrowTowardThatBase_InThrowOrder()
    {
        // 二塁封殺→一塁転送（併殺）: 二塁への送球に二塁の逸れ、一塁への転送に一塁の逸れ。中継の1本目（塁でない）は
        // 絵だけの逸れ。判定の一塁送球（FirstThrowScatter）は転送の後ろに並ぶ＝余って使われない。
        var feed = new ThrowScatterModel.Scatter(11, -3);
        var pivot = new ThrowScatterModel.Scatter(-7, 9);
        var phantomFirst = new ThrowScatterModel.Scatter(99, 99);
        var tl = new PlayTimeline
        {
            Duration = 5, Result = "併殺", ResolvedAt = 4.5,
            Ball = new[]
            {
                Throw(2.0, 2.6, new TimelinePoint(-10, 30), new TimelinePoint(Field.SecondBase.X, Field.SecondBase.Z)),
                Throw(2.9, 3.6, new TimelinePoint(Field.SecondBase.X, Field.SecondBase.Z), new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z)),
                Throw(4.0, 4.5, new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z), new TimelinePoint(0, 18)), // 投手へ返す
            },
        };
        var judged = new[]
        {
            new ThrowScatterAssigner.JudgedThrow(2, feed),
            new ThrowScatterAssigner.JudgedThrow(1, pivot),
            new ThrowScatterAssigner.JudgedThrow(1, phantomFirst),
        };
        var r = ThrowScatterAssigner.Assign(tl, judged, Field, _ => 50, new Xoshiro256Random(1));
        Assert.Equal(feed, r.Ball[0].ThrowScatter);
        Assert.Equal(pivot, r.Ball[1].ThrowScatter);
        Assert.NotNull(r.Ball[2].ThrowScatter);
        Assert.NotEqual(phantomFirst, r.Ball[2].ThrowScatter);
    }

    private static BallSegment Throw(double t0, double t1, TimelinePoint from, TimelinePoint to)
        => new() { Kind = BallSegmentKind.Throw, T0 = t0, T1 = t1, From = from, To = to };

    [Fact]
    public void DisplayOnlyScatter_StaysInsideTheWildThreshold()
    {
        var rng = new Xoshiro256Random(3);
        foreach (var acc in new[] { 1, 30, 50, 99 })
        {
            var cap = ThrowScatterAssigner.DisplayRadialCapSigma * ThrowScatterModel.SigmaCmForAccuracy(acc);
            for (var i = 0; i < 20_000; i++)
                Assert.True(ThrowScatterAssigner.DisplayScatter(acc, rng).RadialCm <= cap + 1e-9);
        }
    }

    [Fact]
    public void PlaybackThrow_EndsAtTheScatteredCatchPoint()
    {
        // 一塁へ向かう送球（三塁→一塁）。横＋40cm（進行方向の左）・浮き30cm。
        var seg = new BallSegment
        {
            Kind = BallSegmentKind.Throw, T0 = 0, T1 = 1, From = new TimelinePoint(-19.4, 19.4), To = new TimelinePoint(19.4, 19.4),
            ThrowScatter = new ThrowScatterModel.Scatter(40, 30),
        };
        var p = EndpointBallSegment.From(seg);
        var end = p.At(1.0);
        // 進行方向は +X ＝ 左は +Y（センター方向）。
        Assert.Equal(19.4, end.X, 6);
        Assert.Equal(19.4 + 0.40, end.Y, 6);
        Assert.Equal(EndpointBallSegment.ScatterCatchHeightM + 0.30, end.H, 6);
        Assert.Equal(EndpointBallSegment.ThrowReleaseHeightM, p.At(0.0).H, 6);

        // 低い送球（沈み45cm）はショートバウンド＝受け手の手前で一度地面に着く。
        var low = EndpointBallSegment.From(seg with { ThrowScatter = new ThrowScatterModel.Scatter(0, -45) });
        Assert.True(low.ScatterIsShortHop);
        Assert.True(Enumerable.Range(60, 30).Select(i => low.At(i / 100.0).H).Min() < 0.02, "手前で弾んでいない");
    }
}

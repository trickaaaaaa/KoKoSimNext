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
/// 悪送球の逸れ方の打ち分け（issue #812・表示専用）。判定で引いた逸れ幅から「手前で弾む／横へ逸れる／
/// 高く抜ける」を決め、どれも球は一塁手の後ろへ逃げる。判定・乱数消費は変えない。
/// </summary>
public sealed class WildThrowPictureTests
{
    private static readonly FieldGeometry Field = new();

    [Theory]
    [InlineData(0.0, WildThrowStyle.ShortHop)]
    [InlineData(WildThrowStyles.ShortHopMaxScatterM - 0.01, WildThrowStyle.ShortHop)]
    [InlineData(WildThrowStyles.ShortHopMaxScatterM, WildThrowStyle.Wide)]
    [InlineData(WildThrowStyles.SailMinScatterM - 0.01, WildThrowStyle.Wide)]
    [InlineData(WildThrowStyles.SailMinScatterM, WildThrowStyle.Sail)]
    [InlineData(30.0, WildThrowStyle.Sail)]
    public void Classify_SplitsByScatter(double scatterM, WildThrowStyle want)
        => Assert.Equal(want, WildThrowStyles.Classify(scatterM));

    private static FieldingPlay WildThrow(double scatterM)
        => FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop, result: BattedBallResult.Error)
            with { ErrorKind = FieldingErrorKind.Throw, WildThrowScatterM = scatterM };

    private static PlayTimeline Build(FieldingPlay play)
        => TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));

    private static double Dist(TimelinePoint a, TimelinePoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static TimelinePoint FirstBase => new(Field.FirstBase.X, Field.FirstBase.Z);

    /// <summary>
    /// 3種とも: 一塁手の捕り損ねが1区間だけ付き、球は一塁へ届くはずだった時刻に塁（脇）を通り、
    /// 区間は途切れずにつながって、一塁手が追いかける先で止まる。高さは型ごとに区別できる。
    /// </summary>
    [Theory]
    [InlineData(3.0, WildThrowStyle.ShortHop)]
    [InlineData(7.0, WildThrowStyle.Wide)]
    [InlineData(12.0, WildThrowStyle.Sail)]
    public void EachStyle_MissesAtFirstAndRunsAway(double scatterM, WildThrowStyle style)
    {
        var play = WildThrow(scatterM);
        var tl = Build(play);
        var wildFrom = tl.Ball.First(b => b.Kind == BallSegmentKind.Throw && b.FromRole == FieldPosition.Shortstop);
        var wild = tl.Ball.SkipWhile(b => !ReferenceEquals(b, wildFrom)).ToList();

        // 区間が途切れない
        for (var i = 1; i < wild.Count && wild[i].T0 < wild[0].T0 + 30; i++)
        {
            if (wild[i].Kind == BallSegmentKind.Throw && wild[i].FromRole is not null) break;   // 返球以降は対象外
            Assert.Equal(wild[i - 1].T1, wild[i].T0, 6);
            Assert.True(Dist(wild[i - 1].To, wild[i].From) < 1e-6, "球が瞬間移動している");
        }

        var miss = Assert.Single(tl.Ball, b => b.MissedBy is not null);
        Assert.Equal(FieldPosition.FirstBase, miss.MissedBy);
        Assert.Equal(style, miss.WildThrow);
        Assert.Null(miss.ToRole);   // 誰も捕らない

        var seg = EndpointBallSegment.From(miss);
        var missT = miss.T0 + (miss.T1 - miss.T0) * miss.MissFraction;
        Assert.True(missT >= TimelineBuilder.ContactAt + play.ThrowArriveSeconds!.Value - 1e-6, "届くはずだった時刻より前に塁を過ぎている");
        var at = seg.At(missT - miss.T0);
        var dBase = Math.Sqrt((at.X - FirstBase.X) * (at.X - FirstBase.X) + (at.Y - FirstBase.Z) * (at.Y - FirstBase.Z));
        switch (style)
        {
            case WildThrowStyle.ShortHop:
                Assert.InRange(at.H, 0.05, 0.6);   // 弾んで上がってくる途中
                Assert.True(dBase < 0.6);
                // 塁の手前で一度地面に着く
                var throwSeg = EndpointBallSegment.From(wildFrom);
                Assert.True(throwSeg.At(wildFrom.T1 - wildFrom.T0).H < 0.05, "弾んでいない");
                break;
            case WildThrowStyle.Wide:
                Assert.InRange(at.H, 0.9, 1.5);
                Assert.InRange(dBase, 1.5, 2.5);   // 塁の脇
                break;
            case WildThrowStyle.Sail:
                Assert.True(at.H >= 3.0, $"一塁手のジャンプで届く高さ {at.H:F2}m");
                Assert.True(dBase < 0.6);
                break;
        }

        // 止まった球を一塁手が追う
        var chase = tl.Moves.Single(m => m.Role == FieldPosition.FirstBase && m.Task == FielderTask.Chase);
        var lastLoose = wild.TakeWhile(b => !(b.Kind == BallSegmentKind.Throw && b.FromRole is not null && !ReferenceEquals(b, wildFrom))).Last();
        Assert.True(Dist(chase.To, lastLoose.To) < 1e-6);
        Assert.True(Dist(lastLoose.To, FirstBase) > 2.0, "球が一塁手の手元に残っている");
    }

    /// <summary>横へ逸れるときだけ、一塁手は塁を離れて球の側へ踏み出す（そのあと追いかける）。</summary>
    [Fact]
    public void Wide_FirstBasemanStepsOffTheBag()
    {
        var tl = Build(WildThrow(7.0));
        var step = tl.Moves.Where(m => m.Role == FieldPosition.FirstBase).OrderBy(m => m.T1)
            .Last(m => m.Task != FielderTask.Chase);
        Assert.InRange(Dist(step.To, FirstBase), 0.8, 1.2);
        var chase = tl.Moves.Single(m => m.Role == FieldPosition.FirstBase && m.Task == FielderTask.Chase);
        Assert.True(chase.T0 >= step.T1 - 1e-9);

        var sail = Build(WildThrow(12.0));
        Assert.DoesNotContain(sail.Moves, m => m.Role == FieldPosition.FirstBase && m.Task != FielderTask.Chase
                                               && Dist(m.To, FirstBase) is > 0.5 and < 1.5);
    }

    /// <summary>同じ逸れ幅なら同じ絵（決定論）。</summary>
    [Fact]
    public void SameScatter_SamePicture()
    {
        var a = Build(WildThrow(7.0));
        var b = Build(WildThrow(7.0));
        Assert.Equal(a.Ball.Count, b.Ball.Count);
        for (var i = 0; i < a.Ball.Count; i++) Assert.Equal(a.Ball[i], b.Ball[i]);
    }

    /// <summary>
    /// 判定側: 逸れ幅は「拾い直しが間に合わなかった悪送球」にだけ載る（拾い直したアウトは #815 の値）。
    /// 回収レースで引いた値の写し＝新しい乱数は引かない（決定論ベースラインが不変であることは別途固定）。
    /// </summary>
    [Fact]
    public void Resolver_ScatterOnlyOnUnrecoveredWildThrows()
    {
        var coeff = new FieldingCoefficients { ThrowErrorBaseProb = 1.0, ErrorMaxProb = 1.0 };
        int wild = 0, recovered = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var p = FieldingResolver.ResolveDetailed(
                new BattedBall { ExitVelocityMps = 28.0 + (seed % 9), LaunchAngleDeg = -6.0 + (seed % 5), BearingDeg = -40.0 + (seed % 80) },
                Field, new Aerodynamics(), BatterAttributes.LeagueAverage,
                Field.StandardAlignment(new FielderAttributes { ThrowAccuracy = 50 }), coeff, new Xoshiro256Random(seed));
            if (p.WildThrowScatterM is { } sc)
            {
                wild++;
                Assert.Equal(FieldingErrorKind.Throw, p.ErrorKind);
                Assert.True(sc >= 0);
                Assert.Null(p.RecoveredThrowScatterM);
            }
            else
            {
                Assert.NotEqual(FieldingErrorKind.Throw, p.ErrorKind);
            }
            if (p.RecoveredThrowScatterM is not null) recovered++;
        }
        Assert.True(wild > 0 && recovered > 0, $"前提が崩れている wild={wild} recovered={recovered}");
    }
}

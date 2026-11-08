using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 一塁手の捕球ミス（issue #810 / design-14 P1-6b 追記）。内野ゴロ→一塁送球の判定は捕球ロール→送球ロールの
/// 2段で止まっていて、送球自体は正確なのに受け手の一塁手が落とす／ショートバウンドを捕れない失策を
/// 表現できなかった。3段目として追加し、失策は処理野手（遊撃等）ではなく受け手の一塁手に付く。
/// </summary>
public sealed class ReceiveErrorTests
{
    private readonly ITestOutputHelper _out;
    public ReceiveErrorTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    /// <summary>遊撃が確実に処理し一塁送球が間に合う強いゴロ（ThrowAccuracyErrorTests と同じ当たり）。</summary>
    private static FieldingPlay Grounder(FieldingCoefficients coeff, int firstBaseCatching, ulong seed)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 34.0, LaunchAngleDeg = -4.0, BearingDeg = -12.0 },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.AlignmentFrom(new System.Collections.Generic.Dictionary<FieldPosition, FielderAttributes>
            {
                [FieldPosition.FirstBase] = new FielderAttributes { Catching = firstBaseCatching, ThrowAccuracy = 95 },
            }),
            coeff, new Xoshiro256Random(seed));

    /// <summary>捕球・送球の2段を無効化し、受け手ロール（3段目）だけを引かせる。</summary>
    private static FieldingCoefficients ReceiveOnly(double receiveBase, double maxProb = 0.30) => new()
    {
        ErrorBaseProb = 0, ErrorMinProb = 0, ErrorMaxProb = maxProb,
        ThrowErrorBaseProb = 0,
        ReceiveErrorBaseProb = receiveBase,
    };

    private static double ErrorRate(FieldingCoefficients coeff, int firstBaseCatching, int trials = 6000)
    {
        var errors = 0;
        for (ulong seed = 1; seed <= (ulong)trials; seed++)
            if (Grounder(coeff, firstBaseCatching, seed).Result == BattedBallResult.Error) errors++;
        return (double)errors / trials;
    }

    // ===== ① 既定0で恒等・正値で内訳ロール1本 =====

    [Fact]
    public void ReceiveErrorDisabled_IsIdenticalAcrossFirstBaseCatching()
    {
        // base=0 では受け手ロールを一切引かない＝一塁手のCatchingを変えても結果が1件も変わらない（恒等の担保）。
        // GroundHops は参照型（配列）フィールドで record の既定 Equals が参照比較になるため、
        // 判定に関わる主要フィールドだけを比較する（ThrowAccuracyErrorTests と同じ趣旨・比較対象だけ変更）。
        var coeff = ReceiveOnly(0.0);
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var a = Grounder(coeff, 10, seed);
            var b = Grounder(coeff, 90, seed);
            Assert.Equal(a.Result, b.Result);
            Assert.Equal(a.ErrorKind, b.ErrorKind);
            Assert.Equal(a.ErrorFielderRole, b.ErrorFielderRole);
            Assert.Equal(a.FielderRole, b.FielderRole);
            Assert.Equal(a.FieldedAtSeconds, b.FieldedAtSeconds);
            Assert.Equal(a.ThrowArriveSeconds, b.ThrowArriveSeconds);
        }
    }

    [Fact]
    public void ZeroBase_DrawsNoExtraRandom_ComparedToPositiveBase()
    {
        var zero = new CountingRandomSource(new Xoshiro256Random(777));
        var full = new CountingRandomSource(new Xoshiro256Random(777));

        var disabled = FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 34.0, LaunchAngleDeg = -4.0, BearingDeg = -12.0 },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.AlignmentFrom(new System.Collections.Generic.Dictionary<FieldPosition, FielderAttributes>
            {
                [FieldPosition.FirstBase] = new FielderAttributes { Catching = 50, ThrowAccuracy = 95 },
            }),
            ReceiveOnly(0.0), zero);
        var enabled = FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 34.0, LaunchAngleDeg = -4.0, BearingDeg = -12.0 },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.AlignmentFrom(new System.Collections.Generic.Dictionary<FieldPosition, FielderAttributes>
            {
                [FieldPosition.FirstBase] = new FielderAttributes { Catching = 50, ThrowAccuracy = 95 },
            }),
            ReceiveOnly(1.0, maxProb: 1.0), full);

        _out.WriteLine($"消費: 無効={zero.Stats.Draws} / 有効={full.Stats.Draws}"
                       + $" 種別: {disabled.ErrorKind} → {enabled.ErrorKind}");

        Assert.NotEqual(FieldingErrorKind.Receive, disabled.ErrorKind);
        Assert.Equal(FieldingErrorKind.Receive, enabled.ErrorKind);
        // 受け手ロールはちょうど1本＝0のときは引いていない（乱数消費順が従来と一致する根拠）。
        Assert.Equal(zero.Stats.Draws + 1, full.Stats.Draws);
    }

    // ===== ② 守備力連動（一塁手のCatchingが低いほど増える） =====

    [Fact]
    public void LowerFirstBaseCatching_ErrsMore()
    {
        var coeff = ReceiveOnly(0.15);
        var poor = ErrorRate(coeff, firstBaseCatching: 10);
        var avg = ErrorRate(coeff, firstBaseCatching: 50);
        var elite = ErrorRate(coeff, firstBaseCatching: 95);

        _out.WriteLine($"捕球低{poor:F3} / 平均{avg:F3} / 捕球高{elite:F3}");
        Assert.True(poor > avg, $"捕球低{poor:F3} が平均{avg:F3} 以下");
        Assert.True(avg > elite, $"平均{avg:F3} が捕球高{elite:F3} 以下");
    }

    // ===== ③ 失策の帰属は一塁手（処理野手とは別人） =====

    [Fact]
    public void Receive_AttributesTheErrorToTheFirstBaseman_NotTheProcessingFielder()
    {
        var coeff = ReceiveOnly(1.0, maxProb: 1.0);
        var play = Grounder(coeff, firstBaseCatching: 10, seed: 1);

        Assert.Equal(FieldingErrorKind.Receive, play.ErrorKind);
        Assert.Equal(FieldPosition.FirstBase, play.ErrorFielderRole);
        Assert.NotEqual(FieldPosition.FirstBase, play.FielderRole);
        Assert.NotEqual(play.FielderRole, play.ErrorFielderRole);

        var text = TimelineBuilder.DescribeResult(play);
        _out.WriteLine($"{play.FielderRole} が処理・{play.ErrorFielderRole} が落球 → 「{text}」");
        Assert.EndsWith("のエラー", text);
    }

    // ===== ④ 一塁手自身が処理した打球には受け手が別にいない＝対象外 =====

    [Fact]
    public void FirstBasemanFieldingItself_NeverDrawsReceiveError()
    {
        // 一塁線寄りの弱いゴロ＝一塁手自身が処理する（UnassistedPutoutTests と同じ当たり）。
        var coeff = ReceiveOnly(1.0, maxProb: 1.0);
        for (ulong seed = 1; seed <= 300; seed++)
        {
            var play = FieldingResolver.ResolveDetailed(
                new BattedBall { ExitVelocityMps = 22.0, LaunchAngleDeg = 3.0, BearingDeg = 38.0 },
                Field, Aero, BatterAttributes.LeagueAverage,
                Field.StandardAlignment(new FielderAttributes { Fielding = 50, Speed = 50, ArmStrength = 50 }),
                coeff, new Xoshiro256Random(seed));
            Assert.Equal(FieldPosition.FirstBase, play.FielderRole);
            Assert.NotEqual(FieldingErrorKind.Receive, play.ErrorKind);
        }
    }

    // ===== ⑤ タイムライン: 送球は正しく一塁へ届き、届いた後にこぼす =====

    [Fact]
    public void Timeline_ThrowArrivesAtFirst_ThenBallIsDeflectedThereAndChased()
    {
        var coeff = ReceiveOnly(1.0, maxProb: 1.0);
        var play = Grounder(coeff, firstBaseCatching: 10, seed: 1);
        Assert.Equal(FieldingErrorKind.Receive, play.ErrorKind);

        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play));

        // 送球は一塁手（受け手）へ届く（Bobble と同じ「送球は正しく届く」扱い）。
        var throwToFirst = Assert.Single(tl.Ball,
            b => b.Kind == BallSegmentKind.Throw && b.ToRole == FieldPosition.FirstBase);
        var arrive = throwToFirst.T1;

        // 届いた直後に弾いて数mこぼす Roll 区間（FromRole=一塁手）がある。
        var dropSeg = Assert.Single(tl.Ball,
            b => b.Kind == BallSegmentKind.Roll && b.FromRole == FieldPosition.FirstBase);
        Assert.Equal(arrive, dropSeg.T0, 3);

        // 「エラー」判定は弾いた瞬間（= dropSeg.T0）に出る＝びっくりマークの時刻と一致する。
        var err = Assert.Single(tl.Judgements, j => j.Kind == JudgementKind.Error);
        _out.WriteLine($"送球到達 {arrive:F2}s ／ エラー表示 {err.T:F2}s");
        Assert.Equal(dropSeg.T0, err.T, 3);

        // 一塁手が拾い直しに向かうレッグがある。
        Assert.Contains(tl.Moves, m => m.Task == FielderTask.Chase && m.Role == FieldPosition.FirstBase);
    }
}

using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 俯瞰ビューに描かれる走者と、エンジンが判定に使った走者が**同じ時計で動く**こと（Issue #316）。
///
/// 以前は表示だけが「27.43/速度 + 0.35、開始=接触+0.10」という第4の式を持ち、判定側より
/// 0.58〜1.63 秒遅かった。そのため「際どい」と判定されたプレーが画面では際どく見えず、
/// [#314] で入れた送球の矛盾ガードが常時発火する部品になっていた。
/// ここが緑なら、判定オーバーレイの margin と絵の前後関係が一致する。
/// </summary>
public sealed class RunnerLegMatchesVerdictTests
{
    private const double ContactAtSeconds = 0.85;
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Baserunning = new();
    private static readonly FieldingCoefficients Fielding = new();

    private static Player RunnerWith(int speed, int baserunning)
        => new() { Name = "走者", Speed = speed, Baserunning = baserunning };

    private static PlayTimeline BaseTimeline(FieldingPlay play)
        => TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: true,
            TimelineBuilder.DescribeResult(play));

    /// <summary>ラベル <paramref name="label"/> の走者の最終レッグ終端（＝到達時刻）。</summary>
    private static double ArriveOf(PlayTimeline tl, string label)
        => tl.Runners.Where(r => r.Label == label && r.T1 > r.T0).Max(r => r.T1);

    // ── 塁上の走者: HomePlayResolver.RunnerTimeSeconds とビット一致 ──

    [Theory]
    [InlineData(3, 4)]   // 三塁→本塁（クロスプレー）
    [InlineData(2, 4)]   // 二塁→本塁
    [InlineData(1, 4)]   // 一塁→本塁
    [InlineData(1, 3)]   // 一塁→三塁（Issue #89）
    public void RunnerFromBase_LegEndsExactlyWhenTheVerdictSaysHeArrives(int fromBase, int toBase)
    {
        var play = FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: 78.0, fieldedAtSeconds: 3.4);

        foreach (var speed in new[] { 20, 50, 80, 99 })
        foreach (var baserunning in new[] { 10, 50, 90 })
        {
            var runner = RunnerWith(speed, baserunning);
            var moves = new List<RunnerMove> { new(runner, fromBase, toBase, Out: false) };
            var tl = TimelineBuilder.AppendRunnerLegs(BaseTimeline(play), moves, Field, Baserunning, Fielding);

            var expected = ContactAtSeconds
                           + HomePlayResolver.RunnerTimeSeconds(runner, fromBase, toBase, Field, Baserunning);

            Assert.Equal(expected, ArriveOf(tl, "走1"));   // 許容差なし＝判定とまったく同じ時刻
        }
    }

    /// <summary>
    /// G1（内野前進のゴロ本塁レース）は走者に追加スタート遅延が乗る。判定がそれを課したなら
    /// 絵も同じだけ遅れて出発する（<c>RunnerMove.ExtraStartDelaySeconds</c> 経由）。
    /// </summary>
    [Fact]
    public void RunnerWithExtraStartDelay_LegMatchesTheVerdictToo()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop);
        var runner = RunnerWith(50, 50);
        var delay = Baserunning.HomeGrounderStartDelaySeconds;
        var moves = new List<RunnerMove>
        {
            new(runner, 3, 4, Out: false) { ExtraStartDelaySeconds = delay },
        };
        var tl = TimelineBuilder.AppendRunnerLegs(BaseTimeline(play), moves, Field, Baserunning, Fielding);

        var expected = ContactAtSeconds
                       + HomePlayResolver.RunnerTimeSeconds(runner, 3, 4, Field, Baserunning, delay);
        Assert.Equal(expected, ArriveOf(tl, "走1"));

        // 追加遅延ぶん確実に遅い（遅延を無視して描いていないことの担保）。
        var noDelay = TimelineBuilder.AppendRunnerLegs(
            BaseTimeline(play), new List<RunnerMove> { new(runner, 3, 4, Out: false) },
            Field, Baserunning, Fielding);
        Assert.Equal(delay, ArriveOf(tl, "走1") - ArriveOf(noDelay, "走1"), 9);
    }

    // ── 打者走者: ExtraBaseResolver の塁打数判定とビット一致 ──

    [Theory]
    [InlineData(2)]   // 二塁打
    [InlineData(3)]   // 三塁打
    public void BatterRunner_LegEndsExactlyWhenTheExtraBaseModelSaysHeArrives(int toBase)
    {
        foreach (var speed in new[] { 20, 50, 80, 99 })
        {
            var batter = RunnerWith(speed, 50);
            var play = FieldingPlayFixtures.OutfieldHit(
                FieldPosition.CenterField, landingX: 0, landingZ: 78.0, fieldedAtSeconds: 3.4,
                result: toBase == 2 ? BattedBallResult.Double : BattedBallResult.Triple);

            var moves = new List<RunnerMove> { new(batter, 0, toBase, Out: false) };
            var tl = TimelineBuilder.AppendRunnerLegs(BaseTimeline(play), moves, Field, Baserunning, Fielding);

            // ExtraBaseResolver.ResolveBases が塁打数を決めるのに使った到達時刻そのもの。
            var perBase = RunnerClock.SegmentSeconds(
                batter.ToBatter(Baserunning.AbilityEffect).SpeedToFirstMps(), 1, 2, ExtraBaseResolver.BatterContextOf(Fielding), Field);
            var expected = ContactAtSeconds + play.BatterToFirstSeconds;
            for (var b = 1; b < toBase; b++) expected += perBase;

            Assert.Equal(expected, ArriveOf(tl, "打"));   // 許容差なし＝判定とまったく同じ時刻
        }
    }

    // ── 向きの回帰（#316 で走者は「速くなる」方向へ動く） ──

    /// <summary>
    /// 修正前の表示式（27.43/速度 + 0.35・開始 接触+0.10）より必ず速く着く。
    /// 「表示だけが遅い」という一方向の乖離が消えたことの回帰。
    /// </summary>
    [Theory]
    [InlineData(3, 4, 0.58)]
    [InlineData(2, 4, 0.93)]
    [InlineData(1, 4, 1.28)]
    public void DisplayIsFasterThanTheOldFourthClock(int fromBase, int toBase, double atLeastSeconds)
    {
        var play = FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: 78.0, fieldedAtSeconds: 3.4);
        var runner = RunnerWith(50, 50);
        var moves = new List<RunnerMove> { new(runner, fromBase, toBase, Out: false) };
        var tl = TimelineBuilder.AppendRunnerLegs(BaseTimeline(play), moves, Field, Baserunning, Fielding);

        // #316 以前の表示式をそのまま書き下したもの。
        var oldPerBase = Field.BaseDistanceM / runner.ToFielder(Baserunning.AbilityEffect).SprintSpeedMps + 0.35;
        var oldArrive = ContactAtSeconds + 0.10 + (toBase - fromBase) * oldPerBase;

        var gained = oldArrive - ArriveOf(tl, "走1");
        Assert.True(gained >= atLeastSeconds,
            $"{fromBase}塁→{toBase}塁で詰まった差が {gained:F3}s（期待 {atLeastSeconds}s 以上）");
    }

    /// <summary>
    /// 待機レッグ（塁上で出発を待つ区間）の終端＝移動レッグの開始で、隙間も重なりも無い。
    /// スタート遅延を式から動かしたときに走者が一瞬消える／二重に描かれる退行を防ぐ。
    /// </summary>
    [Fact]
    public void StandingLeg_ConnectsSeamlesslyToTheFirstMovingLeg()
    {
        var play = FieldingPlayFixtures.OutfieldHit(
            FieldPosition.CenterField, landingX: 0, landingZ: 78.0, fieldedAtSeconds: 3.4);
        var runner = RunnerWith(50, 50);
        var moves = new List<RunnerMove> { new(runner, 1, 3, Out: false) };
        var tl = TimelineBuilder.AppendRunnerLegs(BaseTimeline(play), moves, Field, Baserunning, Fielding);

        var mine = tl.Runners.Where(r => r.Label == "走1").OrderBy(r => r.T0).ToList();
        var standing = mine[0];
        Assert.Equal(0.0, standing.T0, 9);
        // 先頭のレッグは元の塁から始まる（＝接触前に盤面から消えない, #25）。終端は二次リード位置
        // （時計が塁間からリードぶんを引いているので、絵でも出しておく）。
        Assert.Equal(Field.FirstBase.X, standing.From.X, 9);
        Assert.Equal(Field.FirstBase.Z, standing.From.Z, 9);
        var leadM = System.Math.Sqrt(
            System.Math.Pow(standing.To.X - standing.From.X, 2)
            + System.Math.Pow(standing.To.Z - standing.From.Z, 2));
        Assert.Equal(Baserunning.HomeLeadDistanceM, leadM, 6);
        for (var i = 1; i < mine.Count; i++)
        {
            Assert.Equal(mine[i - 1].T1, mine[i].T0, 9);   // 隙間も重なりも無い
            Assert.Equal(mine[i - 1].To.X, mine[i].From.X, 9); // 位置も繋がる（ワープしない）
            Assert.Equal(mine[i - 1].To.Z, mine[i].From.Z, 9);
        }
    }
}

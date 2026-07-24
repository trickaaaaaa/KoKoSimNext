using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 自責点（ER, issue #69）の簡易規則: 失策で出塁した走者、および失策連鎖（design-14 P1-6）で
/// 延命した走者の得点は自責から除外する。それ以外（盗塁・スクイズ等の走塁のみに起因する得点や、
/// 通常出塁からの得点）はすべて自責に算入する。<see cref="ForcedOutcome"/> で打席結果を固定し、
/// 走者の由来を確定させたうえで PitchingLine.Runs / EarnedRuns を検証する。
/// </summary>
public sealed class EarnedRunsTests
{
    [Fact]
    public void ReachedOnError_ThenHomeRun_ErrorRunnerIsUnearned_HomeRunHitterIsEarned()
    {
        var prog = new MatchProgression(
            DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), 1UL);

        prog.ForceNext(ForcedOutcome.ReachedOnError);
        prog.Advance(); // 打者1: 失策出塁（一塁）。

        prog.ForceNext(ForcedOutcome.HomeRun);
        prog.Advance(); // 打者2: 本塁打。打者1（失策出塁）＋打者2（本塁打）の2点。

        var result = prog.BuildResult();
        var pitcher = Assert.Single(result.HomePitching); // 先発1人のまま（自校=先攻・相手=後攻で守備）。

        Assert.Equal(2, pitcher.Runs);
        Assert.Equal(1, pitcher.EarnedRuns); // 失策出塁の走者の得点1つだけ自責から除外。
    }

    [Fact]
    public void AllCleanReaches_EarnedRunsEqualsRuns()
    {
        var prog = new MatchProgression(
            DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), 1UL);

        prog.ForceNext(ForcedOutcome.Single);
        prog.Advance(); // 打者1: 単打（一塁、失策なし）。

        prog.ForceNext(ForcedOutcome.HomeRun);
        prog.Advance(); // 打者2: 本塁打。2点とも失策非経由。

        var result = prog.BuildResult();
        var pitcher = Assert.Single(result.HomePitching);

        Assert.Equal(2, pitcher.Runs);
        Assert.Equal(2, pitcher.EarnedRuns); // 全員クリーン出塁＝ER=R。
    }

    [Fact]
    public void ErrorChain_TaintsPreExistingCleanRunner_WhenLaterScores()
    {
        // 失策連鎖（design-14 P1-6）を確定発火させ、打席前から塁上にいた「元はクリーンな」走者が
        // 同じ失策で追加進塁（延命）した場合に自責対象外へ切り替わることを確認する。
        var ctx = new GameContext
        {
            Baserunning = new BaserunningCoefficients
            {
                ErrorExtraAdvanceProb = 1.0,
                ErrorExtraAdvanceAccuracySlope = 0.0,
                FirstToThirdOnSingle = 0.0, // 一塁走者の通常進塁は必ず二塁止まり（連鎖の起点を固定）。
            },
        };
        var prog = new MatchProgression(DeterminismCards.Team("A"), DeterminismCards.Team("H"), ctx, 1UL);

        prog.ForceNext(ForcedOutcome.Single);
        prog.Advance(); // 打者1（R1）: 単打で一塁（クリーン出塁）。

        prog.ForceNext(ForcedOutcome.ReachedOnError);
        prog.Advance(); // 打者2: 失策出塁。連鎖(prob=1.0)でR1は二塁→三塁へ延命進塁、打者2は一塁→二塁。

        prog.ForceNext(ForcedOutcome.HomeRun);
        prog.Advance(); // 打者3: 本塁打。R1(延命)・打者2(失策出塁)・打者3(クリーン)の3点。

        var result = prog.BuildResult();
        var pitcher = Assert.Single(result.HomePitching);

        Assert.Equal(3, pitcher.Runs);
        Assert.Equal(1, pitcher.EarnedRuns); // 自責は打者3の分のみ。R1・打者2は自責対象外。
    }
}

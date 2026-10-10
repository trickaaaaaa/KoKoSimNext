using System.Linq;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #642: 最終回以降の裏は、勝ち越しの走者が本塁に達した時点で試合終了（公認野球規則 5.08(b)）。
/// それ以降の走者の得点は記録しない。例外は本塁打だけ（打者を含む全走者の得点を認める）。
///
/// <para>旧実装はサヨナラ判定を打席の決着後にしか行わず、走塁解決が返した得点を全部足していた。
/// 6-6・無死一二塁から三塁打で 8-6 のサヨナラになっていた（タイブレークの開始局面そのもの）。</para>
/// </summary>
public sealed class WalkoffTruncationTests
{
    private static ScenarioStart TiedBottom9(bool first, bool second, bool third) => new()
    {
        Inning = 9, IsTop = false, Outs = 0,
        OnFirst = first, OnSecond = second, OnThird = third,
        AwayScore = 6, HomeScore = 6,
    };

    private static GameResult PlayForced(ScenarioStart scenario, ForcedOutcome outcome, ulong seed = 1UL)
    {
        var prog = new MatchProgression(
            DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), seed, scenario);
        prog.ForceNext(outcome);
        while (prog.Advance()) { }
        return prog.BuildResult();
    }

    /// <summary>スコア・イニング得点・得点した走者・打点・投手の失点がすべて「1点」で揃う。</summary>
    private static void AssertOneRunWalkoff(GameResult r)
    {
        Assert.Equal(6, r.AwayRuns);
        Assert.Equal(7, r.HomeRuns);
        Assert.Equal(1, r.HomeLineScore[^1]);
        Assert.Equal(1, r.HomeBatting.Sum(b => b.Runs));
        Assert.Equal(1, r.AwayPitching.Sum(pl => pl.Runs));
    }

    [Theory]
    [InlineData(ForcedOutcome.Triple)]
    [InlineData(ForcedOutcome.Double)]
    public void TiedBottomNinth_FirstAndSecond_ExtraBaseHit_EndsOneRunUp(ForcedOutcome hit)
    {
        // 二塁走者＝同点の1点ではなく、6-6 なので二塁走者が還った時点で 7-6＝勝ち越し。一塁走者の得点は認めない。
        var r = PlayForced(TiedBottom9(first: true, second: true, third: false), hit);
        AssertOneRunWalkoff(r);
        Assert.Equal(1, r.HomeBatting.Sum(b => b.Rbi));
    }

    [Fact]
    public void TiedBottomNinth_BasesLoaded_Triple_EndsOneRunUp()
    {
        var r = PlayForced(TiedBottom9(first: true, second: true, third: true), ForcedOutcome.Triple);
        AssertOneRunWalkoff(r);
        Assert.Equal(1, r.HomeBatting.Sum(b => b.Rbi));
    }

    [Fact]
    public void TiedBottomNinth_FirstAndSecond_HomeRun_CountsEveryRunner()
    {
        var r = PlayForced(TiedBottom9(first: true, second: true, third: false), ForcedOutcome.HomeRun);
        Assert.Equal(9, r.HomeRuns);
        Assert.Equal(3, r.HomeBatting.Sum(b => b.Runs));
        Assert.Equal(3, r.HomeBatting.Sum(b => b.Rbi));
        Assert.Equal(3, r.AwayPitching.Sum(pl => pl.Runs));
    }

    /// <summary>
    /// 打ち切りは最終回以降の裏だけ。規定回前の裏は、同点からの複数得点がそのまま入る。
    /// </summary>
    [Fact]
    public void BeforeRegulation_MultiRunHitIsNotTruncated()
    {
        var scenario = TiedBottom9(first: true, second: true, third: false) with { Inning = 5 };
        var prog = new MatchProgression(
            DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), 1UL, scenario);
        prog.ForceNext(ForcedOutcome.Triple);
        prog.Advance();
        Assert.Equal(8, prog.Current!.HomeScore);
    }

    /// <summary>
    /// 打席の途中のサヨナラ（暴投・捕逸での生還）でも試合は終わる。旧実装は打席の決着まで続け、
    /// 勝ち越した後も同じ打席の打球で得点が積み増せた。打ち切った打席は記録されない（3アウト目の
    /// 盗塁死で打席が未決着のまま終わるのと同じ扱い）。
    /// </summary>
    [Fact]
    public void TiedBottomNinth_WildPitchScoresWinningRun_EndsMidPlateAppearance()
    {
        var hits = 0;
        for (ulong seed = 1; seed <= 200 && hits < 5; seed++)
        {
            var r = PlayForced(TiedBottom9(first: false, second: false, third: true), ForcedOutcome.WildPitch, seed);
            if (r.WildPitchCount == 0 || !r.HomeWon) continue;
            // 暴投が出ても、その前に打席が決着した（同じ球で四球など）シードは対象外。
            if (r.Log.Any(e => e.Inning == 9 && !e.IsTop)) continue;
            hits++;
            AssertOneRunWalkoff(r);
        }
        Assert.True(hits >= 5, $"暴投でサヨナラになるシードが足りない（{hits} 件）");
    }

    [Fact]
    public void ScorersCountedAtWalkoff_KeepsLeadRunnersFirst()
    {
        var p1 = DeterminismCards.Team("X").BattingOrder[0];
        var p2 = DeterminismCards.Team("X").BattingOrder[1];
        var p3 = DeterminismCards.Team("X").BattingOrder[2];
        var moves = new[]
        {
            new RunnerMove(p3, 0, 3, false),   // 打者（還らない）
            new RunnerMove(p1, 1, 4, false),   // 一塁走者
            new RunnerMove(p2, 2, 4, false),   // 二塁走者＝先に還る
        };
        var counted = GameEngine.ScorersCountedAtWalkoff(moves, 1);
        Assert.Same(p2, Assert.Single(counted).Runner);
        Assert.Equal(2, GameEngine.ScorersCountedAtWalkoff(moves, null).Count);
    }
}

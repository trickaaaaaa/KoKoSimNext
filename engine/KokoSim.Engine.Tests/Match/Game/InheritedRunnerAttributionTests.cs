using System.Linq;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 引き継ぎ走者（inherited runner）の失点帰属（issue inherited-runner-run-attribution）。
/// 野球規則 9.16(g): 塁上の走者を出したのは前の投手なので、その走者の生還は前の投手の失点・自責点になる。
/// 救援投手に付くのは自分が出した走者（と打者走者）ぶんだけ。
/// <see cref="ForcedOutcome"/> で打席結果を固定し、走者の由来（誰が出したか）を確定させて
/// PitchingLine.Runs / EarnedRuns を検証する（<see cref="EarnedRunsTests"/> と同じ作法）。
/// </summary>
public sealed class InheritedRunnerAttributionTests
{
    private static MatchProgression NewProg(ulong seed = 1UL, GameContext? ctx = null)
        => new(DeterminismCards.Team("A"), DeterminismCards.Team("H"), ctx ?? new GameContext(), seed);

    /// <summary>後攻（守備側）の投手を控えの reliever へ交代する。</summary>
    private static Player Relieve(MatchProgression prog)
    {
        var opt = prog.SubstitutionOptions(teamIsAway: false);
        var reliever = opt.PitcherCandidates.First(p => p.Name == "HR1");
        Assert.True(prog.ChangePitcher(teamIsAway: false, reliever));
        return reliever;
    }

    private static (PitchingLine starter, PitchingLine reliever) Lines(GameResult result)
    {
        var starter = result.HomePitching.Single(p => p.Name == "HP");
        var reliever = result.HomePitching.Single(p => p.Name == "HR1");
        return (starter, reliever);
    }

    [Fact]
    public void BasesLoadedWalk_AfterRelief_ChargesTheStarter_NotTheReliever()
    {
        var prog = NewProg();

        // 先発が無死満塁を作る（四球3つ）。
        prog.ForceNext(ForcedOutcome.Walk); prog.Advance();
        prog.ForceNext(ForcedOutcome.Walk); prog.Advance();
        prog.ForceNext(ForcedOutcome.Walk); prog.Advance();

        Relieve(prog);

        // 救援が押し出し四球。三塁走者（先発が出した）が生還。
        prog.ForceNext(ForcedOutcome.Walk); prog.Advance();

        var (starter, reliever) = Lines(prog.BuildResult());
        Assert.Equal(1, starter.Runs);
        Assert.Equal(1, starter.EarnedRuns);
        Assert.Equal(0, reliever.Runs);
        Assert.Equal(0, reliever.EarnedRuns);
        Assert.Equal(1, reliever.Walks); // 四球そのものは救援の記録。
        Assert.Equal(3, starter.Walks);
    }

    [Fact]
    public void InheritedRunnerScoresOnHomeRun_RunnerToStarter_BatterToReliever()
    {
        var prog = NewProg();

        prog.ForceNext(ForcedOutcome.Single); prog.Advance(); // 先発が単打で走者を出す。
        Relieve(prog);
        prog.ForceNext(ForcedOutcome.HomeRun); prog.Advance(); // 救援が2ラン被弾。

        var (starter, reliever) = Lines(prog.BuildResult());
        Assert.Equal(1, starter.Runs);   // 引き継ぎ走者ぶん。
        Assert.Equal(1, starter.EarnedRuns);
        Assert.Equal(1, reliever.Runs);  // 打者走者（本塁打）ぶんだけ。
        Assert.Equal(1, reliever.EarnedRuns);
        Assert.Equal(1, reliever.HomeRunsAllowed);
    }

    [Fact]
    public void InheritedErrorRunner_IsUnearnedForTheStarter()
    {
        var prog = NewProg();

        prog.ForceNext(ForcedOutcome.ReachedOnError); prog.Advance(); // 先発のとき失策出塁。
        Relieve(prog);
        prog.ForceNext(ForcedOutcome.HomeRun); prog.Advance();

        var (starter, reliever) = Lines(prog.BuildResult());
        Assert.Equal(1, starter.Runs);
        Assert.Equal(0, starter.EarnedRuns); // 失策で出た走者＝責任投手側でも自責外。
        Assert.Equal(1, reliever.Runs);
        Assert.Equal(1, reliever.EarnedRuns);
    }

    [Fact]
    public void PinchRunner_InheritsTheOriginalRunnersResponsiblePitcher()
    {
        var prog = NewProg();

        prog.ForceNext(ForcedOutcome.Walk); prog.Advance(); // 先発が四球で走者を出す。

        // 攻撃側（先攻）が代走を送る。責任投手は元走者（＝先発）を引き継ぐ。
        var opt = prog.SubstitutionOptions(teamIsAway: true);
        Assert.True(opt.CanPinchRun);
        var pr = opt.Reserves.First(p => p.Name == "APR");
        Assert.True(prog.PinchRun(teamIsAway: true, opt.Runners[0].BaseIndex, pr));

        Relieve(prog);
        prog.ForceNext(ForcedOutcome.HomeRun); prog.Advance();

        var (starter, reliever) = Lines(prog.BuildResult());
        Assert.Equal(1, starter.Runs);
        Assert.Equal(1, reliever.Runs);
    }

    [Fact]
    public void RunnersPutOnByTheReliever_AreTheRelieversOwn()
    {
        var prog = NewProg();

        prog.ForceNext(ForcedOutcome.Strikeout); prog.Advance(); // 先発は1人だけ投げる。
        Relieve(prog); // 走者なしで交代。
        prog.ForceNext(ForcedOutcome.Walk); prog.Advance();
        prog.ForceNext(ForcedOutcome.HomeRun); prog.Advance();

        var (starter, reliever) = Lines(prog.BuildResult());
        Assert.Equal(0, starter.Runs);
        Assert.Equal(2, reliever.Runs);
        Assert.Equal(2, reliever.EarnedRuns);
    }

    [Fact]
    public void BattingAround_SamePlayerReachingAgainstReliever_IsTheRelieversRunner()
    {
        // 打者一巡: 先発のとき出塁した打者が生還（or アウト）したあと、救援のときに再び出塁して生還したら
        // その得点は救援に付く（古い責任投手が残らない）。
        var prog = NewProg();

        // 先発: 打者1 単打 → 打者2 本塁打（打者1 生還・先発 2失点）。
        prog.ForceNext(ForcedOutcome.Single); prog.Advance();
        prog.ForceNext(ForcedOutcome.HomeRun); prog.Advance();
        // 残り7人を三振で流し、打者1が再び回ってくる手前で交代したいが同じ半イニングに収める必要はない。
        // 単純に打者3〜9を三振（3アウトで攻守交代し、次は自校の守備なので相手打線を流す）。
        // ここでは同じ半イニング内での一巡を作るため、先発のまま打者3〜9を歩かせず単打で回す。
        for (var i = 0; i < 7; i++) { prog.ForceNext(ForcedOutcome.HomeRun); prog.Advance(); }
        // 打者1 が再び打席（同じ半イニング）。ここで交代し、打者1 が単打→打者2 本塁打。
        Relieve(prog);
        prog.ForceNext(ForcedOutcome.Single); prog.Advance();
        prog.ForceNext(ForcedOutcome.HomeRun); prog.Advance();

        var (starter, reliever) = Lines(prog.BuildResult());
        Assert.Equal(9, starter.Runs);   // 単打1＋本塁打8。
        Assert.Equal(2, reliever.Runs);  // 打者1（今度は救援が出した）＋打者2。
    }

    /// <summary>
    /// 帰属を付け替えても投手の失点合計は相手の得点と常に一致する（本盗・重盗・暴投の生還も
    /// 責任投手へ付く＝取りこぼしが無い）。采配ありカードで継投・盗塁・暴投を含む試合を複数シード回す。
    /// </summary>
    [Theory]
    [InlineData("tactics")]
    [InlineData("pitch-tactics")]
    [InlineData("avg")]
    public void PitcherRunsSum_AlwaysEqualsOpponentRuns(string card)
    {
        foreach (var seed in DeterminismCards.Seeds())
        {
            var r = DeterminismCards.Run(card, seed);
            Assert.True(r.AwayRuns == r.HomePitching.Sum(p => p.Runs),
                $"{card} seed={seed}: away {r.AwayRuns} vs home pitchers {r.HomePitching.Sum(p => p.Runs)}");
            Assert.True(r.HomeRuns == r.AwayPitching.Sum(p => p.Runs),
                $"{card} seed={seed}: home {r.HomeRuns} vs away pitchers {r.AwayPitching.Sum(p => p.Runs)}");
            Assert.All(r.HomePitching.Concat(r.AwayPitching), p => Assert.True(p.EarnedRuns <= p.Runs && p.EarnedRuns >= 0));
        }
    }
}

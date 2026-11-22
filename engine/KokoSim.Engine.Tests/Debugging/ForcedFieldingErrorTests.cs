using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 強制発動 FieldingError（issue #1247 の撮影用）: 打球の物理を通したうえで最初の捕球を失策にする。
/// 打球が前に飛ばなかった打席では効かず次の打席へ持ち越す。
/// </summary>
public sealed class ForcedFieldingErrorTests
{
    private static readonly ScenarioStart Top1 = new() { Inning = 1, IsTop = true, Outs = 0 };

    private static (int Errors, int Pas) PlayUntilError(ulong seed, bool force, int maxPas = 12)
    {
        var prog = new MatchProgression(
            DeterminismCards.Team("A"), DeterminismCards.Team("H"), new GameContext(), seed, Top1);
        if (force) prog.ForceNext(ForcedOutcome.FieldingError);
        for (var pa = 1; pa <= maxPas; pa++)
        {
            if (!prog.Advance()) break;
            var r = prog.BuildResult();
            if (r.AwayErrors + r.HomeErrors > 0) return (r.AwayErrors + r.HomeErrors, pa);
        }
        return (0, maxPas);
    }

    [Fact]
    public void FieldingError_MakesAnErrorOnTheFirstBallsInPlay_ForEverySeed()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var (errors, _) = PlayUntilError(seed, force: true);
            Assert.True(errors == 1, $"seed {seed}: 失策が出ない（errors={errors}）");
        }
    }

    [Fact]
    public void WithoutForce_ErrorsAreRare_SoTheForceIsWhatMakesThem()
    {
        var withError = 0;
        for (ulong seed = 1; seed <= 10; seed++)
            if (PlayUntilError(seed, force: false).Errors > 0) withError++;
        Assert.True(withError <= 5, $"強制なしでも {withError}/10 で失策＝強制の効きを測れていない");
    }

    [Fact]
    public void FieldingError_LeavesOtherCoefficientsUntouched()
    {
        var c = new KokoSim.Engine.Match.Fielding.FieldingCoefficients();
        Assert.Same(c, ForcedOutcomes.ApplyFielding(c, ForcedOutcome.BuntInPlay));
        var f = ForcedOutcomes.ApplyFielding(c, ForcedOutcome.FieldingError);
        Assert.Equal(1.0, f.ErrorMinProb);
        Assert.Equal(0.0, f.TunnelShareProb);
        Assert.Equal(c.ThrowErrorBaseProb, f.ThrowErrorBaseProb);
    }
}

using System.Text;
using KokoSim.Balance;
using KokoSim.Config;
using Xunit;

namespace KokoSim.Engine.Tests.Balance
{
    public sealed class ProbeTuneTest
    {
        [Fact]
        public void Probe()
        {
            var sb = new StringBuilder();
            var only = new System.Collections.Generic.HashSet<string> { "catcher_arm", "steal_fast" };
            var result = AbilitySweep.Run(5000, 42, coefficientsPath: null, only);
            foreach (var (cond, low, high) in result.Abilities)
            {
                sb.AppendLine($"{cond.Id}: low(={cond.Low}) RunsFor={low.RunsFor:F2} RunsAgainst={low.RunsAgainst:F2} stealForLow={low.StealAttemptsFor:F2}/{low.StealSuccessRateFor:P0} stealAgainstLow={low.StealAttemptsAgainst:F2}/{low.StealSuccessRateAgainst:P0}");
                sb.AppendLine($"{cond.Id}: high(={cond.High}) RunsFor={high.RunsFor:F2} RunsAgainst={high.RunsAgainst:F2} stealForHigh={high.StealAttemptsFor:F2}/{high.StealSuccessRateFor:P0} stealAgainstHigh={high.StealAttemptsAgainst:F2}/{high.StealSuccessRateAgainst:P0}");
                sb.AppendLine($"{cond.Id}: deltaRunsFor={high.RunsFor-low.RunsFor:F2} deltaRunsAgainst={high.RunsAgainst-low.RunsAgainst:F2}");
            }

            Assert.Fail(sb.ToString());
        }
    }
}

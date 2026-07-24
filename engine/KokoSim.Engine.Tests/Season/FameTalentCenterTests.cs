using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 監督の名声→新入生の質(talentCenter)フィードバック（issue #127・設計書04 §2.1）。
/// 「勝つ→名声→良い新入生」の正フィードバックに、暴走防止の頭打ち（cap）が効いていることを検証する。
/// </summary>
public sealed class FameTalentCenterTests
{
    private static readonly RosterCoefficients C = new();

    [Fact]
    public void Fame_Zero_MatchesDefault()
    {
        // 不変条件#2: 名声フィードバックoff(Fame=0)は従来のtalentCenter既定値と厳密一致する。
        Assert.Equal(C.TalentCenterDefault, C.TalentCenterFromFame(0), 9);
    }

    [Fact]
    public void TalentCenter_IncreasesMonotonically_WithFame()
    {
        var prev = C.TalentCenterFromFame(0);
        foreach (var fame in new double[] { 10, 25, 50, 75, 100 })
        {
            var cur = C.TalentCenterFromFame(fame);
            Assert.True(cur > prev, $"Fame={fame} でtalentCenterが増えていない（{prev}→{cur}）");
            prev = cur;
        }
    }

    [Fact]
    public void TalentCenter_CapsNearSpan_AtMaxFame()
    {
        // 頭打ち（cap）は実装の必須要件（issue #127）。Fame=100でも既定+span(=40)を超えない。
        var atMax = C.TalentCenterFromFame(100);
        Assert.True(atMax <= C.TalentCenterDefault + C.FameTalentCenterSpan,
            $"Fame=100のtalentCenter({atMax})が上限({C.TalentCenterDefault + C.FameTalentCenterSpan})を超えた");
        // 控えめな幅（owner決定 1-A）: 32→40程度に収まる。
        Assert.InRange(atMax, 38.0, 40.0);
    }

    [Fact]
    public void TalentCenter_DiminishingReturns_MarginalGainShrinks()
    {
        // 逓減カーブ（線形→漸近）: 名声が上がるほど、同じ+25の伸びに対するtalentCenter増分は小さくなる。
        var gainLow = C.TalentCenterFromFame(25) - C.TalentCenterFromFame(0);
        var gainHigh = C.TalentCenterFromFame(100) - C.TalentCenterFromFame(75);
        Assert.True(gainHigh < gainLow, $"逓減していない（低名声帯+{gainLow:F2} vs 高名声帯+{gainHigh:F2}）");
    }

    [Fact]
    public void TalentCenter_NeverExceedsCap_AboveFameRange()
    {
        // Fameは仕様上0-100だが、式自体もFameの想定域を超えて暴走しないことを確認（安全側）。
        var beyond = C.TalentCenterFromFame(1000);
        Assert.True(beyond <= C.TalentCenterDefault + C.FameTalentCenterSpan + 1e-9);
    }
}

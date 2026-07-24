using KokoSim.Config;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// data/draft.yaml → DraftCoefficients の読込検証（設計書20, 不変条件#3/#4 のデータ駆動を担保）。
/// </summary>
public sealed class DraftLoaderTests
{
    [Fact]
    public void ParsesInlineYaml_OverridesFields()
    {
        const string yaml = """
            draft:
              ability_weight: 0.6
              performance_weight: 0.4
              ceiling_blend: 0.5
              candidate_threshold: 55.0
              nomination_midpoint: 62.0
            """;

        var c = DraftLoader.Parse(yaml);

        Assert.Equal(0.6, c.AbilityWeight, 6);
        Assert.Equal(0.4, c.PerformanceWeight, 6);
        Assert.Equal(0.5, c.CeilingBlend, 6);
        Assert.Equal(55.0, c.CandidateThreshold, 6);
        Assert.Equal(62.0, c.NominationMidpoint, 6);
    }

    [Fact]
    public void UnspecifiedFields_KeepCsharpDefaults()
    {
        // 一部だけ指定 → 未指定は C# 既定（Unity 実プレイの真値）のまま。
        const string yaml = """
            draft:
              ability_weight: 0.7
            """;

        var c = DraftLoader.Parse(yaml);
        var def = new KokoSim.Engine.Career.Draft.DraftCoefficients();

        Assert.Equal(0.7, c.AbilityWeight, 6);
        Assert.Equal(def.PerformanceWeight, c.PerformanceWeight, 6);
        Assert.Equal(def.FirstRoundThreshold, c.FirstRoundThreshold, 6);
        Assert.Equal(def.NominationSpread, c.NominationSpread, 6);
    }

    [Fact]
    public void LoadsRepositoryDraftFile()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("draft.yaml");
        var c = DraftLoader.LoadFromFile(path);

        // 数値の健全性（バンド境界は降順、加重は正）。
        Assert.True(c.AbilityWeight > 0 && c.PerformanceWeight > 0);
        Assert.True(c.FirstRoundThreshold > c.UpperRoundThreshold);
        Assert.True(c.UpperRoundThreshold > c.MiddleRoundThreshold);
        Assert.True(c.MiddleRoundThreshold > c.CandidateThreshold);
        Assert.InRange(c.CeilingBlend, 0.0, 1.0);
    }
}

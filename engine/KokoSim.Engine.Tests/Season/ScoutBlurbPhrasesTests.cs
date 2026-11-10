using System.IO;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// スカウト候補の寸評の語彙（issue #867・design-04 §2.4）。data/scout-blurbs.yaml が
/// <see cref="ScoutModel.PickBlurb"/> の参照しうる全置き場（軸4×水準3）を持つこと、
/// 文言の選択が決定論であることを固定する。
/// </summary>
public sealed class ScoutBlurbPhrasesTests
{
    private static readonly ScoutBlurbPhrases Phrases = ScoutBlurbPhrasesParser.Parse(
        File.ReadAllText(Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("scout-blurbs.yaml")));

    private static readonly string[] Axes = { "batting", "mobility", "defense", "pitching" };
    private static readonly string[] Buckets = { "high", "mid", "low" };

    [Fact]
    public void Yaml_HasEveryAxisBucketCombination()
    {
        var keys = Phrases.Keys.ToHashSet();
        foreach (var axis in Axes)
            foreach (var bucket in Buckets)
                Assert.True(keys.Contains($"scout.blurb.{axis}.{bucket}"), $"data/scout-blurbs.yaml に scout.blurb.{axis}.{bucket} が無い");
    }

    [Fact]
    public void BlurbText_IsDeterministic()
    {
        var state = BuildState();
        var candidate = state.Candidates[0];
        var a = ScoutModel.BlurbText(candidate, Phrases);
        var b = ScoutModel.BlurbText(candidate, Phrases);
        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    [Fact]
    public void BlurbText_NonEmpty_ForAllGeneratedCandidates()
    {
        var state = BuildState();
        foreach (var c in state.Candidates)
            Assert.NotEmpty(ScoutModel.BlurbText(c, Phrases));
    }

    private static ScoutingState BuildState() => ScoutModel.BuildCandidates(
        yearIndex: 4, field: ScoutField.Omakase, level: 3, slots: 4, homePrefectureId: 13,
        homeTier: KokoSim.Engine.Nation.Tier.C, talentCenter: new RosterCoefficients().TalentCenterDefault,
        contractQualityAdd: 6, roster: new RosterCoefficients(), c: new ScoutCoefficients(),
        strengthCoeff: new KokoSim.Engine.Nation.TeamStrengthCoefficients(),
        rng: new Xoshiro256Random(424242));
}

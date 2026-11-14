using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// 試合 HUD の特殊能力チップを場面で絞る（issue #781）。打者帯は打撃だけ・投手帯は投手だけ。
/// 分類は data/skills.yaml（カタログ）から引く＝同名の「尻上がり」（打撃/投手）を取り違えない。
/// </summary>
public sealed class SkillCategoryFilterTests
{
    private static readonly Dictionary<Skill, SkillCategory> Catalog = SkillCatalogParser
        .Parse(File.ReadAllText(Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("skills.yaml")))
        .ToDictionary(e => e.Id, e => e.Category);

    private static SkillCategory? CategoryOf(Skill s) => Catalog.TryGetValue(s, out var c) ? c : null;

    // 打撃と投手の両方（同名の尻上がり2種）＋守備・チーム・体質を見えている能力として持つ二刀流
    private static readonly SkillSet TwoWay = new(
        visible: new[]
        {
            Skill.SlowStarterBat, Skill.SlowStarterPitch, Skill.Grinder, Skill.DeceptiveBall,
            Skill.Moodmaker, Skill.Durable,
        },
        hidden: new[] { Skill.Streaky, Skill.EffectivelyWild });

    [Fact]
    public void BatterBand_ShowsBattingOnly()
        => Assert.Equal(new[] { Skill.SlowStarterBat, Skill.Grinder },
            SkillCategoryFilter.VisibleIn(TwoWay, CategoryOf, SkillCategory.Batting));

    [Fact]
    public void PitcherBand_ShowsPitchingOnly()
        => Assert.Equal(new[] { Skill.SlowStarterPitch, Skill.DeceptiveBall },
            SkillCategoryFilter.VisibleIn(TwoWay, CategoryOf, SkillCategory.Pitching));

    [Fact]
    public void SameNamedSlowStarters_AreTellApartByCategory()
    {
        // 表示名は同じ「尻上がり」。分類で引くので打者帯に投手用が混ざらない
        Assert.Equal(SkillCategory.Batting, Catalog[Skill.SlowStarterBat]);
        Assert.Equal(SkillCategory.Pitching, Catalog[Skill.SlowStarterPitch]);
        Assert.DoesNotContain(Skill.SlowStarterPitch,
            SkillCategoryFilter.VisibleIn(TwoWay, CategoryOf, SkillCategory.Batting));
        Assert.DoesNotContain(Skill.SlowStarterBat,
            SkillCategoryFilter.VisibleIn(TwoWay, CategoryOf, SkillCategory.Pitching));
    }

    [Fact]
    public void HiddenAndUncategorized_AreNeverShown()
    {
        var onlyHidden = new SkillSet(hidden: new[] { Skill.Streaky });
        Assert.Empty(SkillCategoryFilter.VisibleIn(onlyHidden, CategoryOf, SkillCategory.Batting));
        // カタログが読めない（分類が引けない）ときは何も出さない＝どの帯のものか分からない能力を混ぜない
        Assert.Empty(SkillCategoryFilter.VisibleIn(TwoWay, _ => null, SkillCategory.Batting));
        Assert.Empty(SkillCategoryFilter.VisibleIn(null, CategoryOf, SkillCategory.Batting));
    }
}

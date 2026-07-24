using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>DevelopingPlayer.Clone() の往復一致（Issue #223）。練習プレビューの非破壊ドライランが
/// 実選手の現在値を正確に複製できているかを検証する。</summary>
public sealed class DevelopingPlayerCloneTests
{
    private static DevelopingPlayer BuildFullyPopulated()
    {
        var p = new DevelopingPlayer
        {
            Name = "テスト太郎",
            Throws = Handedness.Left,
            Bats = Handedness.Switch,
            GrowthType = GrowthType.Late,
            PersonalityFactor = 1.2,
            IsProdigy = true,
            Leadership = 77,
            Personality = Personality.HotBlood,
            InjuryResistance = 42.5,
        };
        p.Id = 123;
        p.Grade = 2;
        p.HasPitcherBackground = true;
        p.LearnedPitches.Add(new LearnedPitch(PitchType.Slider, 5, -3));
        p.LearnedPitches.Add(new LearnedPitch(PitchType.Fork, -2, 8));
        p.Mental = 61;
        p.MentalExp = 12.5;
        p.MentalCap = 88;
        p.Lead = 55;
        p.LeadExp = 3.25;
        p.LeadCap = 90;
        p.IsCaptain = true;
        p.IsRetired = false;
        p.UniformNumber = 4;
        p.Skills = new SkillSet(visible: new[] { Skill.Grinder }, hidden: new[] { Skill.Monster });
        p.PitchingGrowth = 1.1;
        p.BattingGrowth = 0.9;
        p.DefenseGrowth = 1.05;
        p.Plan = new TrainingPlan { Preset = TrainingPreset.AceDevelopment };
        p.IndividualCoaching = true;
        p.ConditionValue = 0.4;
        p.Injury = InjurySeverity.Moderate;
        p.InjurySite = InjurySite.Shoulder;
        p.InjuryType = InjuryType.Strain;
        p.InjuryWeeksRemaining = 2;
        p.SlumpWeeks = 1;
        p.HasYips = true;
        p.IsPitcher = true; // 明示指名（HasExplicitRole=true になる経路）

        foreach (var k in AbilityKinds.All)
        {
            p.SetLevel(k, 10 + (int)k);
            p.SetCap(k, 90 + (int)k % 5);
            p.AddExp(k, 1.5 * (int)k + 1);
        }
        foreach (FieldPosition pos in System.Enum.GetValues(typeof(FieldPosition)))
        {
            p.SetAptitude(pos, 20 + (int)pos);
            p.SetAptitudeCap(pos, 95 - (int)pos % 3);
            p.AddAptitudeExp(pos, 2.0 * (int)pos + 1);
        }

        return p;
    }

    [Fact]
    public void Clone_RoundTrips_AllMutableState()
    {
        var original = BuildFullyPopulated();

        var clone = original.Clone();

        Assert.Equal(original.Id, clone.Id);
        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.Grade, clone.Grade);
        Assert.Equal(original.Throws, clone.Throws);
        Assert.Equal(original.Bats, clone.Bats);
        Assert.Equal(original.HasPitcherBackground, clone.HasPitcherBackground);
        Assert.Equal(original.GrowthType, clone.GrowthType);
        Assert.Equal(original.PersonalityFactor, clone.PersonalityFactor);
        Assert.Equal(original.IsProdigy, clone.IsProdigy);
        Assert.Equal(original.Mental, clone.Mental);
        Assert.Equal(original.MentalExp, clone.MentalExp);
        Assert.Equal(original.MentalCap, clone.MentalCap);
        Assert.Equal(original.Lead, clone.Lead);
        Assert.Equal(original.LeadExp, clone.LeadExp);
        Assert.Equal(original.LeadCap, clone.LeadCap);
        Assert.Equal(original.Leadership, clone.Leadership);
        Assert.Equal(original.Personality, clone.Personality);
        Assert.Equal(original.IsCaptain, clone.IsCaptain);
        Assert.Equal(original.IsRetired, clone.IsRetired);
        Assert.Equal(original.UniformNumber, clone.UniformNumber);
        Assert.Equal(original.PitchingGrowth, clone.PitchingGrowth);
        Assert.Equal(original.BattingGrowth, clone.BattingGrowth);
        Assert.Equal(original.DefenseGrowth, clone.DefenseGrowth);
        Assert.Equal(original.IndividualCoaching, clone.IndividualCoaching);
        Assert.Equal(original.ConditionValue, clone.ConditionValue);
        Assert.Equal(original.Injury, clone.Injury);
        Assert.Equal(original.InjurySite, clone.InjurySite);
        Assert.Equal(original.InjuryType, clone.InjuryType);
        Assert.Equal(original.InjuryWeeksRemaining, clone.InjuryWeeksRemaining);
        Assert.Equal(original.InjuryResistance, clone.InjuryResistance);
        Assert.Equal(original.SlumpWeeks, clone.SlumpWeeks);
        Assert.Equal(original.HasYips, clone.HasYips);
        Assert.Equal(original.HasExplicitRole, clone.HasExplicitRole);
        Assert.Equal(original.IsPitcher, clone.IsPitcher);
        Assert.Equal(original.Plan, clone.Plan);
        Assert.True(original.Skills.Has(Skill.Grinder) == clone.Skills.Has(Skill.Grinder));
        Assert.True(original.Skills.IsHidden(Skill.Monster) == clone.Skills.IsHidden(Skill.Monster));
        Assert.Equal(original.LearnedPitches, clone.LearnedPitches);

        foreach (var k in AbilityKinds.All)
        {
            Assert.Equal(original.Level(k), clone.Level(k));
            Assert.Equal(original.Cap(k), clone.Cap(k));
            Assert.Equal(original.Exp(k), clone.Exp(k));
        }
        foreach (FieldPosition pos in System.Enum.GetValues(typeof(FieldPosition)))
        {
            Assert.Equal(original.Aptitude(pos), clone.Aptitude(pos));
            Assert.Equal(original.AptitudeCap(pos), clone.AptitudeCap(pos));
            Assert.Equal(original.AptitudeExp(pos), clone.AptitudeExp(pos));
        }
    }

    [Fact]
    public void Clone_IsIndependent_MutatingCloneDoesNotAffectOriginal()
    {
        var original = BuildFullyPopulated();
        var clone = original.Clone();

        clone.SetLevel(AbilityKind.Contact, 999);
        clone.AddExp(AbilityKind.Contact, 500);
        clone.SetAptitude(FieldPosition.Catcher, 1);
        clone.Mental = 1;
        clone.LearnedPitches.Add(new LearnedPitch(PitchType.Curve, 1, 1));
        clone.Skills = clone.Skills.Reveal(Skill.Monster);

        Assert.NotEqual(999, original.Level(AbilityKind.Contact));
        Assert.NotEqual(1, original.Aptitude(FieldPosition.Catcher));
        Assert.NotEqual(1, original.Mental);
        Assert.Equal(2, original.LearnedPitches.Count);
        Assert.True(original.Skills.IsHidden(Skill.Monster));
    }

    [Fact]
    public void Clone_PreservesImplicitRole_WhenNotExplicitlySet()
    {
        var original = new DevelopingPlayer();
        Assert.False(original.HasExplicitRole);

        var clone = original.Clone();

        Assert.False(clone.HasExplicitRole);
        Assert.Equal(original.IsPitcher, clone.IsPitcher);
    }
}

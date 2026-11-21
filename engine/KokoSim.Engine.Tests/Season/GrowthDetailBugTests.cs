using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>育成の細部の不具合（issue #1223）: イップス克服の対象・才能上限の99超え・県外候補の出身県の偏り。</summary>
public sealed class GrowthDetailBugTests
{
    private static readonly GrowthEventCoefficients YipsOnly = new()
    {
        YipsWeeklyProb = 1.0, AwakeningWeeklyProb = 0, BreakthroughWeeklyProb = 0, SlumpWeeklyProb = 0,
        PlateauWeeklyProb = 0, YipsOvercomeWeeklyProb = 1.0,
    };

    private static DevelopingPlayer Slumping(bool pitcher, int throwAccuracy, int control)
    {
        var p = new DevelopingPlayer { SlumpWeeks = 3 };
        PitcherRoleResolver.SeedAptitudeForRole(p, pitcher);
        p.SetLevel(AbilityKind.ThrowAccuracy, throwAccuracy);
        p.SetCap(AbilityKind.ThrowAccuracy, 90);
        p.SetLevel(AbilityKind.Control, control);
        p.SetCap(AbilityKind.Control, 90);
        return p;
    }

    [Fact]
    public void Yips_Overcome_RestoresTheAbilityThatWasLowered_EvenIfRoleFlipped()
    {
        var rng = new Xoshiro256Random(1);
        var p = Slumping(pitcher: false, throwAccuracy: 60, control: 60);
        GrowthEventModel.Week(new[] { p }, rng, YipsOnly);
        Assert.True(p.HasYips);
        Assert.Equal(55, p.Level(AbilityKind.ThrowAccuracy));

        PitcherRoleResolver.SeedAptitudeForRole(p, true);   // 在籍中に投手寄りへ入れ替わる
        Assert.True(PitcherRoleResolver.LeansPitcher(p));
        GrowthEventModel.Week(new[] { p }, rng, YipsOnly);

        Assert.False(p.HasYips);
        Assert.Equal(60, p.Level(AbilityKind.ThrowAccuracy));
        Assert.Equal(60, p.Level(AbilityKind.Control));
    }

    [Fact]
    public void Yips_Overcome_RestoresOnlyTheActualDrop()
    {
        var rng = new Xoshiro256Random(2);
        var p = Slumping(pitcher: false, throwAccuracy: 3, control: 50);
        GrowthEventModel.Week(new[] { p }, rng, YipsOnly);
        Assert.Equal(1, p.Level(AbilityKind.ThrowAccuracy));

        GrowthEventModel.Week(new[] { p }, rng, YipsOnly);
        Assert.Equal(3, p.Level(AbilityKind.ThrowAccuracy));
    }

    [Fact]
    public void Yips_RecordSurvivesSaveRoundTripAndClone()
    {
        var p = Slumping(pitcher: false, throwAccuracy: 60, control: 60);
        GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(3), YipsOnly);

        var clone = p.Clone();
        Assert.Equal(AbilityKind.ThrowAccuracy, clone.YipsAbility);
        Assert.Equal(5, clone.YipsDrop);

        var back = DevelopingPlayerSaves.FromJson(DevelopingPlayerSaves.ToJson(p));
        Assert.Equal(AbilityKind.ThrowAccuracy, back.YipsAbility);
        Assert.Equal(5, back.YipsDrop);
    }

    [Fact]
    public void Yips_Overcome_LegacyPlayerWithoutRecord_StillRecovers()
    {
        var p = Slumping(pitcher: false, throwAccuracy: 55, control: 60);
        p.HasYips = true;
        GrowthEventModel.Week(new[] { p }, new Xoshiro256Random(4), YipsOnly);
        Assert.False(p.HasYips);
        Assert.Equal(60, p.Level(AbilityKind.ThrowAccuracy));
    }

    [Fact]
    public void Intake_Caps_NeverExceed99_ExceptStamina()
    {
        var c = new RosterCoefficients();
        for (var year = 1; year <= 3; year++)
        {
            foreach (var p in ProspectGenerator.Intake(year, c, new Xoshiro256Random((ulong)year), talentCenter: 400))
            {
                Assert.InRange(p.MentalCap, 1, 99);
                Assert.InRange(p.LeadCap, 1, 99);
                foreach (var k in AbilityKinds.All.Where(k => k != AbilityKind.Stamina))
                    Assert.True(p.Cap(k) <= 99, $"{k} cap {p.Cap(k)} (level {p.Level(k)})");
            }
        }
    }

    [Fact]
    public void TransferConversion_Caps_NeverExceed99_ExceptStamina()
    {
        var dp = new DevelopingPlayer { Mental = 99, Lead = 99, Leadership = 99 };
        PitcherRoleResolver.SeedAptitudeForRole(dp, false);
        foreach (var k in AbilityKinds.All)
        {
            dp.SetLevel(k, 99);
            dp.SetCap(k, 99);
        }
        var ai = TransferRosterConversion.ToAiPlayer(dp, 3);
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var back = TransferRosterConversion.ToDevelopingPlayer(
                ai, new Xoshiro256Random(seed), new RosterCoefficients(), new PersonalityCoefficients());
            Assert.InRange(back.MentalCap, 1, 99);
            Assert.InRange(back.LeadCap, 1, 99);
            foreach (var k in AbilityKinds.All.Where(k => k != AbilityKind.Stamina))
                Assert.True(back.Cap(k) <= 99, $"{k} cap {back.Cap(k)} (level {back.Level(k)})");
        }
    }

    [Fact]
    public void RollPrefecture_OutOfHome_IsUniformOverTheOtherPrefectures()
    {
        const int home = 13;
        var count = PrefectureNames.Japanese.Count;
        var c = new ScoutCoefficients { OutOfPrefectureRatio = 1.0 };
        var rng = new Xoshiro256Random(99);
        var hits = new int[count];
        const int n = 46 * 2000;
        for (var i = 0; i < n; i++) hits[ScoutModel.RollPrefecture(home, c, rng)]++;

        Assert.Equal(0, hits[home]);
        var expected = n / (double)(count - 1);
        for (var id = 0; id < count; id++)
        {
            if (id == home) continue;
            Assert.InRange(hits[id], expected * 0.85, expected * 1.15);
        }
    }
}

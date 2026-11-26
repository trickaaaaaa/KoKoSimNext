using System.IO;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season.Events;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>名伯楽との出会い＝出会った年から3年間、毎年の自然増に全分野 +1（design-04 §1.1b・issue #1679）。</summary>
public sealed class ManagerMentorTests
{
    private static ManagerGrowthCoefficients Coeff(double mentorProb) => new()
    {
        BigLossProb = 0, FamousRivalProb = 0, ProObVisitBaseProb = 0, ProObVisitFameSlope = 0, MentorProb = mentorProb,
    };

    private static System.Collections.Generic.IReadOnlyList<ManagerGrowthNotice> Year(Manager m, double mentorProb, ulong seed = 1)
        => ManagerSeasonUpdate.Apply(m, false, false, 2, new Xoshiro256Random(seed), growthCoeff: Coeff(mentorProb),
            attendSeminar: false);

    [Fact]
    public void Mentor_BoostsAllFieldsForThreeYears_ThenStops()
    {
        var withMentor = new Manager();
        var control = new Manager();

        double Gap() => withMentor.CoachingBatting - control.CoachingBatting;
        var gaps = new double[4];
        for (var y = 0; y < 4; y++)
        {
            Year(withMentor, y == 0 ? 1.0 : 0.0);
            Year(control, 0.0);
            gaps[y] = Gap();
        }

        // 出会った年を含む3年、毎年おおよそ +1 ずつ差が開き（上限に近いほど自然増が縮むぶんの誤差だけ許す）、4年目は開かない。
        Assert.InRange(gaps[0], 0.99, 1.0);
        Assert.InRange(gaps[1] - gaps[0], 0.85, 1.0);
        Assert.InRange(gaps[2] - gaps[1], 0.85, 1.0);
        Assert.InRange(gaps[3] - gaps[2], -0.15, 0.0);
        Assert.Equal(withMentor.CoachingPitching - control.CoachingPitching, Gap(), 6);
        Assert.Equal(withMentor.CoachingDefense - control.CoachingDefense, Gap(), 6);
        Assert.Equal(0, withMentor.MentorYearsLeft);
    }

    [Fact]
    public void Mentor_MeetingAndFarewellAreReportedOnce()
    {
        var m = new Manager();

        var y1 = Year(m, 1.0);
        Assert.Contains(y1, n => n.Type == ManagerGrowthEventType.MentorCoach);
        Assert.DoesNotContain(y1, n => n.Type == ManagerGrowthEventType.MentorFarewell);
        Assert.Equal(2, m.MentorYearsLeft);

        var y2 = Year(m, 0.0);
        Assert.DoesNotContain(y2, n => n.Type is ManagerGrowthEventType.MentorCoach or ManagerGrowthEventType.MentorFarewell);
        Assert.Equal(1, m.MentorYearsLeft);

        var y3 = Year(m, 0.0);
        Assert.Contains(y3, n => n.Type == ManagerGrowthEventType.MentorFarewell);
        Assert.Equal(0, m.MentorYearsLeft);

        var y4 = Year(m, 0.0);
        Assert.DoesNotContain(y4, n => n.Type == ManagerGrowthEventType.MentorFarewell);
    }

    [Fact]
    public void Mentor_DoesNotMeetAgainWhileActive_ButStillDrawsTheSameRandoms()
    {
        var m = new Manager { MentorYearsLeft = 2 };
        var fired = ManagerGrowthEvents.Yearly(m, false, 2, new Xoshiro256Random(1), Coeff(1.0), attendSeminar: false);
        Assert.DoesNotContain(fired, n => n.Type == ManagerGrowthEventType.MentorCoach);
        Assert.Equal(2, m.MentorYearsLeft);

        // 付いている間も乱数は引く＝名伯楽の抽選が後ろの乱数をずらさない（出会う年と同じ本数）。
        var a = new Xoshiro256Random(5);
        var b = new Xoshiro256Random(5);
        ManagerGrowthEvents.Yearly(new Manager { MentorYearsLeft = 1 }, false, 2, a, Coeff(1.0), attendSeminar: false);
        ManagerGrowthEvents.Yearly(new Manager(), false, 2, b, Coeff(1.0), attendSeminar: false);
        Assert.Equal(a.NextInt(0, 1_000_000), b.NextInt(0, 1_000_000));
    }

    [Fact]
    public void Mentor_NoMentor_LeavesTheCareerSimUntouched()
    {
        var a = new Manager();
        var b = new Manager();
        Year(a, 0.0);
        ManagerSeasonUpdate.Apply(b, false, false, 2, new Xoshiro256Random(1), growthCoeff: Coeff(0.0), attendSeminar: false);
        Assert.Equal(b.CoachingBatting, a.CoachingBatting, 9);
        Assert.Equal(0, a.MentorYearsLeft);
    }

    [Fact]
    public void Save_MentorYearsLeft_Roundtrips()
    {
        var state = new SaveState { Manager = new Manager { MentorYearsLeft = 2 } };
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        Assert.Equal(2, s.Manager.MentorYearsLeft);
    }

    [Fact]
    public void Save_OldSaveWithoutMentorYears_ReadsAsZero()
    {
        var json = SaveCodec.Serialize(new SaveState { Manager = new Manager { MentorYearsLeft = 2 } });
        var old = json.Replace(",\"mentorYearsLeft\":2", "")
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":51");
        Assert.DoesNotContain("mentorYearsLeft", old);
        Assert.True(SaveCodec.TryDeserialize(old, out var s, out var err), err);
        Assert.Equal(0, s.Manager.MentorYearsLeft);
    }

    [Fact]
    public void Events_MeetingTellsTheYears_AndFarewellExists()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "data", "events.yaml"))) dir = dir.Parent;
        var events = EventTableParser.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "data", "events.yaml")));

        var meet = events.Single(e => e.Id == "mgr_mentor");
        Assert.All(meet.Variants, v => Assert.Contains(v.Lines, l => l.Say.Contains("{mentor_years}")));
        Assert.Contains(events, e => e.Id == "mgr_mentor_farewell");
        Assert.Contains("mentor_years", EventTextFormat.KnownKeys);
        Assert.Equal(3, new ManagerGrowthCoefficients().MentorYears);
    }
}

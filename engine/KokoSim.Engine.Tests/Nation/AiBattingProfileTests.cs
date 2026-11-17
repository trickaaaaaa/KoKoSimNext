using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の打撃プロフィール（issue ai-roster-power-top-tier-all-sluggers, 2026-08-18）:
/// パワー⇄ミートの振り替え（打撃指標不変）＋5能力の凸凹配分（総和不変・専用Fork）。
/// </summary>
public sealed class AiBattingProfileTests
{
    [Fact]
    public void Draw_IsDeterministic_AndMeanOne()
    {
        var rc = new RosterCoefficients { AiConcentration = 0.15 };
        var a = AiBattingProfile.Draw(new Xoshiro256Random(11), rc);
        var b = AiBattingProfile.Draw(new Xoshiro256Random(11), rc);
        Assert.Equal(a, b);
        var mean = (a.Contact + a.Power + a.Discipline + a.Fielding + a.Catching) / 5.0;
        Assert.Equal(1.0, mean, 9);
        Assert.NotEqual(a.Contact, a.Power);
    }

    [Fact]
    public void Draw_IsFlat_WhenConcentrationIsZero()
    {
        var rc = new RosterCoefficients { AiConcentration = 0.0 };
        Assert.Equal(AiBattingProfile.Flat, AiBattingProfile.Draw(new Xoshiro256Random(11), rc));
    }

    [Fact]
    public void PositionPlayers_HaveContactAbovePower_ByTheSkew_AndTheBattingIndexIsPreserved()
    {
        // 同じ乱数列で skew あり/なしを生成し、平均で Contact +skew／Power −skew、.35C+.35P は不変（誤差内）。
        var flat = new RosterCoefficients { AiPowerSkew = 0.0, AiConcentration = 0.0 };
        var skew = new RosterCoefficients { AiPowerSkew = 8.0, AiConcentration = 0.0 };
        double dc = 0, dp = 0; const int n = 200;
        for (var i = 0; i < n; i++)
        {
            var p0 = StrengthTeamFactory.PositionPlayer(FieldPosition.CenterField, 60, new Xoshiro256Random((ulong)i), new PersonalityCoefficients(), 1, "a", null, flat);
            var p1 = StrengthTeamFactory.PositionPlayer(FieldPosition.CenterField, 60, new Xoshiro256Random((ulong)i), new PersonalityCoefficients(), 1, "a", null, skew);
            dc += p1.Contact - p0.Contact; dp += p1.Power - p0.Power;
        }
        Assert.InRange(dc / n, 7.0, 9.0);
        Assert.InRange(dp / n, -9.0, -7.0);
    }

    [Fact]
    public void Concentration_SpreadsAbilities_WithoutMovingTheirSum()
    {
        var flat = new RosterCoefficients { AiPowerSkew = 0.0, AiConcentration = 0.0 };
        var conc = new RosterCoefficients { AiPowerSkew = 0.0, AiConcentration = 0.15 };
        double sumDiff = 0, absDiff = 0; const int n = 200;
        for (var i = 0; i < n; i++)
        {
            var p0 = StrengthTeamFactory.PositionPlayer(FieldPosition.Shortstop, 60, new Xoshiro256Random((ulong)i), new PersonalityCoefficients(), 1, "a", null, flat);
            var p1 = StrengthTeamFactory.PositionPlayer(FieldPosition.Shortstop, 60, new Xoshiro256Random((ulong)i), new PersonalityCoefficients(), 1, "a", null, conc);
            sumDiff += (p1.Contact + p1.Power + p1.Discipline + p1.Fielding + p1.Catching) - (p0.Contact + p0.Power + p0.Discipline + p0.Fielding + p0.Catching);
            absDiff += System.Math.Abs(p1.Power - p0.Power);
        }
        Assert.InRange(sumDiff / n, -2.0, 2.0);   // 総和は保たれる（丸め誤差内）
        Assert.True(absDiff / n > 4.0, $"凸凹が付いていない（|ΔPower| 平均 {absDiff / n:F1}）");
    }

    [Fact]
    public void SeniorGrowthFactor_KeepsThirdYearsAheadOfSecondYears_InMostSchools()
    {
        // 旧 0.4 では逆算配分の伸びが2年に集中し、2年>3年 が 6〜8割の校で起きていた（B/A で顕著）。
        var deps = new AiRosterDeps();
        var inverted = 0; const int schools = 20;
        for (var id = 1; id <= schools; id++)
        {
            var school = new School { Id = 900 + id, Name = "校", PrefectureId = 0, Strength = 72, Fame = 50 };
            var roster = AiRosterFactory.Bootstrap(school, 1, deps);
            for (var y = 2; y <= 4; y++) AiRosterCycle.AdvanceOneYear(roster, school, y, deps);
            var g2 = roster.Players.Where(p => p.Grade == 2).Average(p => p.Snapshot.Power);
            var g3 = roster.Players.Where(p => p.Grade == 3).Average(p => p.Snapshot.Power);
            if (g2 > g3) inverted++;
        }
        Assert.True(inverted <= schools / 4, $"2年>3年 の校 {inverted}/{schools}");
    }
}

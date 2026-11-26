using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 校風の型（能力配分の学校単位の偏り・issue #1691）。6指標（<see cref="TeamStrengthProfile"/> の
/// 打撃/投手/守備/機動）はこのオフセットがそのまま出る（可視の偏りが狙いそのもの）が、6指標の<b>加重平均
/// （総合＝School.Strength 相当）はほぼ動かない</b>ように各型の配分比を校正してある
/// （<see cref="SchoolStyleCoefficients"/> のコメント参照）。1校・1シードだと丸め・個体差の乱数ノイズで
/// ばらつくため（打者の型・個人の凸凹も同じ乱数が絡む）、ここでは issue 本文の言い回し通り
/// 「<b>平均</b>」で検証する（多数の学校IDで平均をとる）。
/// </summary>
public sealed class SchoolStyleArchetypeTests
{
    private static readonly TeamStrengthCoefficients StrengthCoeff = new();
    private const int SampleCount = 24;

    private static Team CreateTeam(double strength, ulong seed, SchoolStyle? style, double roll = 1.0)
        => StrengthTeamFactory.Create(strength, "T", new Xoshiro256Random(seed), style: style, styleIntensityRoll: roll);

    private static TeamStrength Profile(Team team) => ScoutedTeamProfile.Compute(team, StrengthCoeff);

    private static double Average(Func<ulong, double> metric)
        => Enumerable.Range(0, SampleCount).Select(i => metric(0x1000_0000UL + (ulong)i * 0x9E37_79B9UL)).Average();

    [Theory]
    [InlineData(SchoolStyle.PowerHitting)]
    [InlineData(SchoolStyle.DefensiveMinded)]
    [InlineData(SchoolStyle.SmallBall)]
    [InlineData(SchoolStyle.AceDependent)]
    public void StyledTeam_OverallMatchesStandard_OnAverage(SchoolStyle style)
    {
        // 加算(生成)の比を校正してあるので、多数の学校で平均すれば総合はほぼ一致する（1校単体は丸め・
        // 打者の型の個体差ノイズでずれてよい＝ここは「平均」で見る）。
        foreach (var strength in new[] { 40.0, 65.0, 85.0 })
        {
            var baseline = Average(seed => Profile(CreateTeam(strength, seed, SchoolStyle.Standard, 1.0)).Overall);
            var styled = Average(seed => Profile(CreateTeam(strength, seed, style, 1.0)).Overall);
            // 校正は線形（加重和がちょうど0）だが、強豪校では一方の能力が天井(99)に当たって非線形に
            // 目減りする（特に豪腕依存のエースは球速やわらかい天井＋他3能力の硬い天井を同時に踏む）。
            // 許容は実測（SchoolStyleArchetypeTests 自身）で決めた帯＝天井の目減り分だけ緩める。
            Assert.True(Math.Abs(baseline - styled) < 3.5,
                $"style={style} strength={strength}: base={baseline:F2} styled={styled:F2}");
        }
    }

    [Fact]
    public void Standard_IsUnaffectedByIntensityRoll()
    {
        // 型なし（Standard/TotalBaseball）は尖り方の抽選値に関わらず常に丸いまま（単体で厳密一致でよい）。
        const ulong seed = 0xA1B2_C3D4UL;
        var r0 = Profile(CreateTeam(70.0, seed, SchoolStyle.Standard, 0.0));
        var r1 = Profile(CreateTeam(70.0, seed, SchoolStyle.Standard, 1.0));
        Assert.Equal(r0.Overall, r1.Overall, 1e-9);
        Assert.Equal(r0.Batting, r1.Batting, 1e-9);
        Assert.Equal(r0.Pitching, r1.Pitching, 1e-9);
    }

    [Fact]
    public void PowerHitting_BattingExceedsPitchingByAtLeastTenOnAverage_AtFullSharpness()
    {
        var battingMinusPitching = Average(seed =>
        {
            var s = Profile(CreateTeam(65.0, seed, SchoolStyle.PowerHitting, roll: 1.0));
            return s.Batting - s.Pitching;
        });
        Assert.True(battingMinusPitching >= 10.0, $"batting-pitching(avg)={battingMinusPitching:F1}");
    }

    [Fact]
    public void DefensiveMinded_PitchingExceedsBattingByAtLeastTenOnAverage_AtFullSharpness()
    {
        var pitchingMinusBatting = Average(seed =>
        {
            var s = Profile(CreateTeam(65.0, seed, SchoolStyle.DefensiveMinded, roll: 1.0));
            return s.Pitching - s.Batting;
        });
        Assert.True(pitchingMinusBatting >= 10.0, $"pitching-batting(avg)={pitchingMinusBatting:F1}");

        var defenseGain = Average(seed =>
            Profile(CreateTeam(65.0, seed, SchoolStyle.DefensiveMinded, roll: 1.0)).Defense
            - Profile(CreateTeam(65.0, seed, SchoolStyle.Standard, roll: 1.0)).Defense);
        Assert.True(defenseGain > 0, $"defense gain(avg)={defenseGain:F1}");
    }

    [Fact]
    public void SmallBall_MobilityExceedsStandardOnAverage_AndBattingIsLower()
    {
        var mobilityGain = Average(seed =>
            Profile(CreateTeam(65.0, seed, SchoolStyle.SmallBall, roll: 1.0)).Mobility
            - Profile(CreateTeam(65.0, seed, SchoolStyle.Standard, roll: 1.0)).Mobility);
        Assert.True(mobilityGain >= 8.0, $"mobility gain(avg)={mobilityGain:F1}");

        var battingDrop = Average(seed =>
            Profile(CreateTeam(65.0, seed, SchoolStyle.Standard, roll: 1.0)).Batting
            - Profile(CreateTeam(65.0, seed, SchoolStyle.SmallBall, roll: 1.0)).Batting);
        Assert.True(battingDrop > 0, $"batting drop(avg)={battingDrop:F1}");
    }

    [Fact]
    public void AceDependent_AceGetsPositiveOffset_OthersGetNegativeOffset()
    {
        // これは乱数ノイズに左右されない構造の確認（生成した選手が正しい符号のオフセットを持っているか）
        // なので複数シードで確認して毎回同じ符号になることを固定する。
        for (var i = 0; i < 6; i++)
        {
            var team = CreateTeam(65.0, 0x5EED_0004UL + (ulong)i * 0x1234UL, SchoolStyle.AceDependent, roll: 1.0);
            var ace = team.BattingOrder[team.PitcherSlot];
            Assert.Equal(FieldPosition.Pitcher, ace.Position);
            Assert.True(ace.StyleOffset.Velocity > 0, $"ace velocity offset={ace.StyleOffset.Velocity}");

            var bullpenPitchers = team.Reserves.Where(p => p.Position == FieldPosition.Pitcher).ToList();
            Assert.NotEmpty(bullpenPitchers);
            Assert.All(bullpenPitchers, p => Assert.True(p.StyleOffset.Velocity < 0, $"bullpen velocity offset={p.StyleOffset.Velocity}"));

            var fielders = team.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher);
            Assert.All(fielders, p => Assert.True(p.StyleOffset.Contact < 0, $"fielder contact offset={p.StyleOffset.Contact}"));
        }
    }

    [Fact]
    public void StandardStyle_PlayersHaveNoStyleOffset()
    {
        var team = CreateTeam(65.0, 0x5EED_0005UL, SchoolStyle.Standard, roll: 1.0);
        Assert.All(team.BattingOrder, p => Assert.True(p.StyleOffset.IsNone));
        Assert.All(team.Reserves, p => Assert.True(p.StyleOffset.IsNone));
    }

    /// <summary>
    /// 校風の型は学校単位で持ち、毎年の新入生と成長に効き続ける（issue #1691 方針）。永続ロスターを
    /// 複数年進めても、打撃⇔投手の偏りが消えない（丸い形へ戻らない）ことを、多数の学校IDの平均で固定する
    /// （1校単体は打者の型の抽選・個人の凸凹の乱数ノイズで大きくばらつく＝11人前後のコホートでは自然な分散）。
    /// </summary>
    [Fact]
    public void PersistentRoster_KeepsPowerHittingBiasOnAverage_AcrossMultipleYears()
    {
        const int schoolSamples = 10;

        (AiSchoolRoster Roster, School School, AiRosterDeps Deps) MakeBootstrapped(int id)
        {
            var deps = new AiRosterDeps();
            var school = new School
            {
                Id = id, Name = $"型持続テスト高校{id}", PrefectureId = 1,
                Strength = 68.0, Fame = 60.0,
                Style = SchoolStyle.PowerHitting, StyleIntensityRoll = 1.0,
            };
            var roster = AiRosterFactory.Bootstrap(school, currentYearIndex: 5, deps);
            return (roster, school, deps);
        }

        double BattingMinusPitching(AiSchoolRoster roster, School school, int yearIndex, AiRosterDeps deps)
        {
            var active = roster.Players.Where(p => p.Grade is >= 1 and <= 3 && !p.Phenom.IsPhenom()).ToList();
            var team = AiTeamBuilder.BuildFrom(active, school, yearIndex, deps);
            var s = ScoutedTeamProfile.Compute(team, deps.TeamStrength);
            return s.Batting - s.Pitching;
        }

        var instances = Enumerable.Range(9100, schoolSamples).Select(MakeBootstrapped).ToList();
        var yearIndex = 5;

        var initialBias = instances.Average(x => BattingMinusPitching(x.Roster, x.School, yearIndex, x.Deps));
        Assert.True(initialBias >= 6.0, $"初期の偏り（平均）が小さすぎる: {initialBias:F1}");

        for (var y = 0; y < 4; y++)
        {
            yearIndex++;
            foreach (var x in instances)
            {
                AiRosterCycle.RetireGraduates(x.Roster);
                AiRosterCycle.AdvanceOneYear(x.Roster, x.School, yearIndex, x.Deps);
            }
            var bias = instances.Average(x => BattingMinusPitching(x.Roster, x.School, yearIndex, x.Deps));
            Assert.True(bias >= 5.0, $"{y + 1}年後に偏り（平均）が消えている（丸い形へ戻った）: {bias:F1}");
        }
    }
}

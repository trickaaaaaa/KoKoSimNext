using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 結束の土台 U0 の分布（OPEN-QUESTIONS Q54 決着 2026-11-14・設計書23 §3.2）。顔ぶれだけで U0≥70 に届く部が
/// 上位約15%、U0&lt;30 に落ちる部が下位約15%、ふつうの部は50付近。
///
/// <para>母集団は実ゲームで試合に出てくるチームの作り方そのもの: 全国生成（seed 2026）の学校を間引いて
/// <see cref="AiRosterFactory.Bootstrap"/>→<see cref="AiTeamBuilder.Build"/> で組んだベンチ入り（打順＋控え）と主将。
/// 自校相当（新入生3学年から能力上位20人・最上級生の統率力最大を主将）も確かめる（自校は主将選びと合宿で
/// 動かせるので目安は緩める）。帯（Heavy）は選手の生成を通らない＝ここが U0 の安全網。</para>
/// </summary>
public sealed class CohesionDistributionTests
{
    private static readonly CohesionCoefficients C = new();
    private readonly ITestOutputHelper _out;

    public CohesionDistributionTests(ITestOutputHelper output) => _out = output;

    private (double Median, double High, double Low) Summarize(string label, IReadOnlyList<double> values)
    {
        var u = values.OrderBy(x => x).ToList();
        var median = u[u.Count / 2];
        var high = u.Count(x => x >= C.HighThreshold) / (double)u.Count;
        var low = u.Count(x => x < C.LowThreshold) / (double)u.Count;
        _out.WriteLine($"{label} n={u.Count}: 中央値 {median:F1} / U0≥70 {high:P1} / U0<30 {low:P1}" +
                       $" / p15 {u[(int)(u.Count * 0.15)]:F1} p85 {u[(int)(u.Count * 0.85)]:F1}");
        return (median, high, low);
    }

    [Fact]
    public void AiSchools_TopAndBottomAbout15Percent()
    {
        var nation = NationGenerator.Generate(new SchoolNameVocab(), new NationCoefficients(), new Xoshiro256Random(2026));
        var deps = new AiRosterDeps();
        var schools = nation.Schools.Where((_, i) => i % 4 == 0).Take(1000).ToList();
        var u0 = new double[schools.Count];
        Parallel.For(0, schools.Count, i =>
        {
            var school = schools[i];
            const int year = 3;
            var team = AiTeamBuilder.Build(AiRosterFactory.Bootstrap(school, year, deps), school, year, deps);
            var members = new List<Player>(team.BattingOrder);
            members.AddRange(team.Reserves);
            if (team.UsesDh && team.StartingPitcher is not null) members.Add(team.StartingPitcher);
            u0[i] = Cohesion.BaseValue(members, team.Captain, C);
        });
        var (median, high, low) = Summarize("AI校", u0);
        Assert.InRange(median, 45.0, 55.0);
        Assert.InRange(high, 0.10, 0.20);
        Assert.InRange(low, 0.10, 0.20);
    }

    [Fact]
    public void OwnSchoolLike_AlsoSpreadsAroundFifty()
    {
        var rc = new RosterCoefficients();
        var u0 = new double[600];
        Parallel.For(0, u0.Length, i =>
        {
            var roster = new List<DevelopingPlayer>();
            for (var g = 1; g <= 3; g++)
            {
                foreach (var p in ProspectGenerator.Intake(g, rc, new Xoshiro256Random((ulong)(500_000 + i * 10 + g))))
                {
                    p.Grade = 4 - g;
                    roster.Add(p);
                }
            }
            // ベンチ入り＝能力上位20人。上級生の精神力は AI校と同じ学年ぶんのアーク（自校は練習で伸ばす量の目安）。
            var members = roster.OrderByDescending(p => p.AverageLevel()).Take(20)
                .Select(dp => AiMentalModel.WithGradeArc(RosterTeamBuilder.ToPlayer(dp, FieldPosition.LeftField), dp.Grade, rc))
                .ToList();
            u0[i] = Cohesion.BaseValue(members, CaptainSelector.SelectFromPlayers(members), C);
        });
        var (median, high, low) = Summarize("自校相当", u0);
        Assert.InRange(median, 43.0, 57.0);
        Assert.InRange(high, 0.07, 0.23);
        Assert.InRange(low, 0.07, 0.23);
    }
}

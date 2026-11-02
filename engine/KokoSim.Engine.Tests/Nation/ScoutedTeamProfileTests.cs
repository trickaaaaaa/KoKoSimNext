using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 実体化した相手校の6指標（練習試合の相手選択レーダー）が、学校の強さに追随し決定論であることを検証する。
/// </summary>
public sealed class ScoutedTeamProfileTests
{
    private static readonly TeamStrengthCoefficients C = new();

    private static School School(int id, double strength)
        => new() { Id = id, Name = "校" + id, PrefectureId = 13, Strength = strength };

    [Fact]
    public void Compute_IsDeterministic_ForSameSchoolAndYear()
    {
        var s = School(101, 62);
        var a = ScoutedTeamProfile.Compute(StrengthTeamFactory.ForSchool(s, yearIndex: 0), C);
        var b = ScoutedTeamProfile.Compute(StrengthTeamFactory.ForSchool(s, yearIndex: 0), C);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_TracksSchoolStrength_OnTheSameScaleAsOwnTeam()
    {
        var weak = ScoutedTeamProfile.Compute(StrengthTeamFactory.ForSchool(School(1, 30), 0), C);
        var strong = ScoutedTeamProfile.Compute(StrengthTeamFactory.ForSchool(School(2, 80), 0), C);

        Assert.True(strong.Overall > weak.Overall);
        // 学校の強さ（raw。圧縮は恒等＝設計書21）と同尺度に収まる。生成中心＝raw で Overall ≒ 中心−5 なので
        // 強度80校は 72〜80 前後（表示ランク A・中身も A/B+＝乖離しない）。
        Assert.InRange(weak.Overall, 15, 45);
        Assert.InRange(strong.Overall, 65, 85);
    }

    [Fact]
    public void Compute_FillsAllSixAxes()
    {
        var t = ScoutedTeamProfile.Compute(StrengthTeamFactory.ForSchool(School(3, 55), 0), C);
        foreach (var v in new[] { t.Batting, t.Pitching, t.Defense, t.Mobility, t.Depth, t.Mental })
            Assert.InRange(v, 1, 100);
    }

    /// <summary>
    /// issue #737: ホームの次戦表示は School.Tier（学校の抽象的な強さ）、試合前画面は実ロスターの総合で、
    /// 同じ相手校のランクが食い違っていた。両画面が引く単一入口 OverallTier が試合前画面の総合と一致し、
    /// School.Tier とは一致しない（＝School.Tier を相手のランクとして出してはいけない）ことを固定する。
    /// </summary>
    [Fact]
    public void OverallTier_MatchesPreviewOverall_NotSchoolTier()
    {
        var s = School(101, 62);
        var team = StrengthTeamFactory.ForSchool(s, yearIndex: 0);
        var overall = ScoutedTeamProfile.Compute(team, C).Overall;

        Assert.Equal(Tiers.FromStrength(overall), ScoutedTeamProfile.OverallTier(team, C));
        Assert.NotEqual(s.Tier, ScoutedTeamProfile.OverallTier(team, C));
    }
}

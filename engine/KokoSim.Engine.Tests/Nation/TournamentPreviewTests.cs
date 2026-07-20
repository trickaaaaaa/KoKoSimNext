using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会プレビュー自動生成（設計書06 §3.5b, mock-tournament-preview.html）。
/// 強さ順の格付け（◎○▲）・3軸合成（校風で profile が変わる）・寸評/リードを検証する。
/// </summary>
public sealed class TournamentPreviewTests
{
    private static School Sch(int id, double strength, SchoolStyle style = SchoolStyle.Standard, string? name = null)
        => new() { Id = id, Name = name ?? $"第{id}高校", PrefectureId = 0, Strength = strength, Style = style };

    private static List<School> Field()
        => new()
        {
            Sch(1, 88, SchoolStyle.PowerHitting, "強打学園"),
            Sch(2, 86, SchoolStyle.DefensiveMinded, "守勝高校"),
            Sch(3, 84, SchoolStyle.AceDependent, "豪腕商業"),
            Sch(4, 70, SchoolStyle.SmallBall, "機動力工"),
            Sch(5, 62, SchoolStyle.Standard, "普通高校"),
            Sch(6, 55, SchoolStyle.Standard, "県立A"),
            Sch(7, 48, SchoolStyle.Standard, "県立B"),
            Sch(8, 40, SchoolStyle.Standard, "県立C"),
        };

    [Fact]
    public void Build_RanksAndMarks_TopSchools()
    {
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会");
        // ◎1・○2・▲1（＋無印は含めない）。
        Assert.Equal(1, p.Contenders.Count(c => c.Mark == ContenderMark.Favorite));
        Assert.Equal(2, p.Contenders.Count(c => c.Mark == ContenderMark.Contender));
        Assert.True(p.Contenders.Count(c => c.Mark == ContenderMark.DarkHorse) <= 1);

        // 優勝候補は最強（シード1）。マーク順に並ぶ。
        var fav = p.Contenders.First(c => c.Mark == ContenderMark.Favorite);
        Assert.Equal("強打学園", fav.Name);
        Assert.Equal(1, fav.Seed);
        Assert.Equal(Tier.A, fav.Tier);
        Assert.Equal(ContenderMark.Favorite, p.Contenders[0].Mark); // 先頭は◎
    }

    [Fact]
    public void Rating_ReflectsSchoolStyle()
    {
        var p = TournamentPreviewBuilder.Build("styleテスト", Field(), berths: 2, "地区大会");
        var power = p.Contenders.First(c => c.Name == "強打学園").Rating;
        var def = p.Contenders.First(c => c.Name == "守勝高校").Rating;

        // 強打待球は打線＞投手陣。守り勝つは守備＞打線。校風が profile を変える。
        Assert.True(power.Batting > power.Pitching, $"強打の打線({power.Batting})が投手({power.Pitching})より上のはず");
        Assert.True(def.Defense > def.Batting, $"守勝の守備({def.Defense})が打線({def.Batting})より上のはず");
    }

    [Fact]
    public void Lead_And_Blurbs_AreGenerated()
    {
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会");
        Assert.Contains("強打学園", p.Lead);          // 優勝候補名がリードに入る
        Assert.Contains("2校", p.Meta);               // 進出枠
        Assert.Contains("地区大会", p.Meta);
        Assert.All(p.Contenders, c => Assert.False(string.IsNullOrEmpty(c.Blurb)));
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var a = TournamentPreviewBuilder.Build("t", Field(), 2, "地区大会");
        var b = TournamentPreviewBuilder.Build("t", Field(), 2, "地区大会");
        Assert.Equal(
            a.Contenders.Select(c => (c.Name, c.Mark, c.Rating.Batting, c.Rating.Pitching, c.Rating.Defense)),
            b.Contenders.Select(c => (c.Name, c.Mark, c.Rating.Batting, c.Rating.Pitching, c.Rating.Defense)));
    }
}

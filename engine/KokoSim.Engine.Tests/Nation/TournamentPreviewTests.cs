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

    /// <summary>
    /// リード文に同じ校名が2度出ない（スクショ tournament-preview-01.png の
    /// 「対するは青葉中央高校と青葉中央高校」の再発防止）。実生成の全国から県を引いて検証する。
    /// </summary>
    [Fact]
    public void Contenders_And_Lead_HaveNoDuplicateSchoolNames()
    {
        var nation = NationGenerator.Generate(
            new SchoolNameVocab(), new NationCoefficients(), new KokoSim.Engine.Core.Xoshiro256Random(2026));

        foreach (var pref in nation.Prefectures)
        {
            var entrants = nation.InPrefecture(pref.Id).ToList();
            if (entrants.Count < 8) continue;

            var p = TournamentPreviewBuilder.Build($"{pref.Name}大会", entrants, berths: 2, "地区大会");
            var names = p.Contenders.Select(c => c.Name).ToList();

            Assert.Equal(names.Count, names.Distinct().Count());   // 格付け校に同名が並ばない

            // リード文でも同じ校名が2回出ていない。
            foreach (var n in names)
                Assert.True(CountOccurrences(p.Lead, n) <= 1,
                    $"{pref.Name}: リード文に「{n}」が複数回出現 → {p.Lead}");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
            count++;
        return count;
    }

    // ===== 注目選手・登録メンバー（設計書06 §3.5b） =====

    [Fact]
    public void NotablePlayers_OnePerContender_WithIdentityAndStats()
    {
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会");

        Assert.Equal(p.Contenders.Count, p.NotablePlayers.Count);
        foreach (var n in p.NotablePlayers)
        {
            Assert.False(string.IsNullOrWhiteSpace(n.Name));
            Assert.InRange(n.UniformNumber, 1, 20);
            Assert.InRange(n.Grade, 1, 3);
            Assert.Matches("^[右左]投[右左両]打$", n.HandednessLabel);
            Assert.False(string.IsNullOrWhiteSpace(n.StatLine));
            Assert.False(string.IsNullOrWhiteSpace(n.Blurb));
            Assert.Contains(n.SchoolName, p.Contenders.Select(c => c.Name));
        }
        // 投手が選ばれた校では最速表記が出る（モックの「最速148km/h」相当）。
        foreach (var n in p.NotablePlayers.Where(x => x.IsPitcher))
            Assert.Contains("最速", n.StatLine);
    }

    [Fact]
    public void NotablePlayer_IsAce_WhenPitchingLeadsTheTeam()
    {
        // 豪腕商業（AceDependent）は投手力が打力を上回る＝エースが看板になり背番号1で出る。
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会");
        var ace = p.NotablePlayers.FirstOrDefault(n => n.SchoolName == "豪腕商業");

        Assert.NotNull(ace);
        Assert.True(ace!.IsPitcher);
        Assert.Equal(1, ace.UniformNumber);
        Assert.Equal("投", ace.PositionLabel);
    }

    [Fact]
    public void Rosters_ListBenchTwenty_WithoutDetailedRatings()
    {
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会");

        Assert.Equal(p.Contenders.Count, p.Rosters.Count);
        foreach (var r in p.Rosters)
        {
            Assert.Equal(20, r.Members.Count);                                  // ベンチ入り20人
            Assert.Equal(20, r.Members.Select(m => m.UniformNumber).Distinct().Count());
            // 背番号昇順で並ぶ（表として読みやすい）。
            Assert.Equal(r.Members.Select(m => m.UniformNumber).OrderBy(x => x), r.Members.Select(m => m.UniformNumber));
            Assert.All(r.Members, m =>
            {
                Assert.False(string.IsNullOrWhiteSpace(m.Name));
                Assert.InRange(m.Grade, 1, 3);
                Assert.Matches("^[右左]投[右左両]打$", m.HandednessLabel);
            });
            Assert.False(string.IsNullOrWhiteSpace(r.SeedLabel));
        }
    }

    /// <summary>
    /// ★展望↔実戦の一致。展望が載せた選手は、実際にその校と対戦したときのラインナップに
    /// 同一氏名・同一背番号で存在する（「展望に載ってた選手だ！」の担保）。
    /// </summary>
    [Fact]
    public void PreviewRoster_MatchesActualOpponentLineup()
    {
        var field = Field();
        const int year = 2;
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", field, berths: 2, "地区大会", yearIndex: year);

        foreach (var roster in p.Rosters)
        {
            var school = field.First(s => s.Name == roster.SchoolName);
            // 実戦で組まれる相手チーム（PlayerMatchResolver と同じ入口）。
            var actual = StrengthTeamFactory.ForSchool(school, year);
            var actualMembers = actual.BattingOrder.Concat(actual.Bullpen).Concat(actual.Bench)
                .Select(x => (x.Name, x.UniformNumber)).OrderBy(x => x.UniformNumber).ToList();

            Assert.Equal(actualMembers, roster.Members.Select(m => (m.Name, m.UniformNumber)).ToList());
        }

        // 注目選手も実ラインナップに実在する。
        foreach (var n in p.NotablePlayers)
        {
            var school = field.First(s => s.Name == n.SchoolName);
            var actual = StrengthTeamFactory.ForSchool(school, year);
            Assert.Contains(actual.BattingOrder.Concat(actual.Bullpen).Concat(actual.Bench),
                x => x.Name == n.Name && x.UniformNumber == n.UniformNumber);
        }
    }

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

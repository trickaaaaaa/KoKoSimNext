using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// リーグ戦の大会パンフレット（issue #783）。ブロックの対戦→星取表の行列化と、
/// 地区予選の展望に勝ち抜き戦の語（優勝・トーナメント・シード・短期決戦）が出ないこと。
/// </summary>
public sealed class LeagueCrossTableTests
{
    // 順位表の並び＝北都(1位)・桜丘(自校・2位)・湘南(3位)・海浜(4位)
    private static LeagueBlockView Block() => new(
        Block: 0,
        Standings: new List<LeagueStandingView>
        {
            new(1, 20, "北都", false, 2, 1, 5, 3, true),
            new(2, 10, "桜丘", true, 2, 1, 0, 3, true),
            new(3, 30, "湘南", false, 1, 2, -5, 3, false),
            new(4, 40, "海浜", false, 0, 0, 0, 0, false),
        },
        Games: new List<LeagueGameView>
        {
            new(0, 0, 0, 10, "桜丘", 20, "北都", true, 5, 3, false, true),    // 桜丘 5-3 北都
            new(0, 0, 0, 30, "湘南", 40, "海浜", false, 0, 0, false, false),  // 未消化
            new(1, 0, 1, 30, "湘南", 20, "北都", true, 0, 7, true, false),    // 北都 7-0 湘南（コールド）
            new(2, 0, 2, 10, "桜丘", 30, "湘南", true, 2, 4, false, true),    // 湘南 4-2 桜丘
        },
        IsManagerBlock: true);

    [Fact]
    public void RowsAndColumns_FollowStandingsOrder_WithDiagonalSelf()
    {
        var t = LeagueCrossTable.Build(Block());
        Assert.Equal(new[] { "北都", "桜丘", "湘南", "海浜" }, t.Rows.Select(r => r.SchoolName));
        for (var i = 0; i < t.Rows.Count; i++)
        {
            Assert.Equal(t.Rows.Count, t.Rows[i].Cells.Count);
            Assert.Equal(CrossCellKind.Self, t.Rows[i].Cells[i].Kind);
        }
        Assert.True(t.Started);
        Assert.True(t.IsManagerBlock);
    }

    [Fact]
    public void Result_IsSeenFromEachRow_WinnerGetsCircle_LoserGetsDot()
    {
        var t = LeagueCrossTable.Build(Block());
        var sakura = t.Rows[1];
        var hokuto = t.Rows[0];
        // 桜丘 5-3 北都: 桜丘の行の北都の列に ○5-3、北都の行の桜丘の列に ●3-5
        Assert.Equal(new CrossCell(CrossCellKind.Win, 5, 3), sakura.Cells[0]);
        Assert.Equal(new CrossCell(CrossCellKind.Loss, 3, 5), hokuto.Cells[1]);
        // 湘南 4-2 桜丘（A側が負け）
        Assert.Equal(new CrossCell(CrossCellKind.Loss, 2, 4), sakura.Cells[2]);
        Assert.Equal(new CrossCell(CrossCellKind.Win, 4, 2), t.Rows[2].Cells[1]);
    }

    [Fact]
    public void Mercy_IsMarkedOnBothSides_AndUnplayedStaysEmpty()
    {
        var t = LeagueCrossTable.Build(Block());
        Assert.Equal(new CrossCell(CrossCellKind.Win, 7, 0, Mercy: true), t.Rows[0].Cells[2]);
        Assert.Equal(new CrossCell(CrossCellKind.Loss, 0, 7, Mercy: true), t.Rows[2].Cells[0]);
        Assert.Equal(CrossCellKind.Unplayed, t.Rows[2].Cells[3].Kind);
        Assert.Equal(CrossCellKind.Unplayed, t.Rows[3].Cells[2].Kind);
        Assert.Equal(CrossCellKind.Unplayed, t.Rows[3].Cells[1].Kind);   // 試合が組まれていない対戦も空欄
    }

    [Fact]
    public void RightColumns_CopyTheStandings()
    {
        var r = LeagueCrossTable.Build(Block()).Rows[1];
        Assert.Equal((2, 1, 0, 2, true, true), (r.Wins, r.Losses, r.RunMargin, r.Rank, r.InQualifyingPlace, r.IsManager));
    }

    [Fact]
    public void NotStarted_WhenNoGameIsPlayed()
    {
        var b = Block() with { Games = Block().Games.Select(g => g with { Played = false }).ToList() };
        Assert.False(LeagueCrossTable.Build(b).Started);
    }

    // ---- 地区予選（リーグ戦）の展望の文言 ----

    private static List<School> Field()
    {
        var styles = new[] { SchoolStyle.PowerHitting, SchoolStyle.DefensiveMinded, SchoolStyle.AceDependent,
            SchoolStyle.SmallBall, SchoolStyle.Standard, SchoolStyle.TotalBaseball };
        var list = new List<School>();
        for (var i = 0; i < 24; i++)
            list.Add(new School { Id = i + 1, Name = $"第{i + 1}高校", PrefectureId = 0, Strength = 90 - i * 2.5, Style = styles[i % styles.Length] });
        return list;
    }

    [Fact]
    public void DistrictLeaguePreview_NeverUsesKnockoutWords()
    {
        var field = Field();
        var p = TournamentPreviewBuilder.Build("秋季横浜地区予選", field, berths: 2, nextStageName: "県大会",
            stage: PreviewStage.DistrictLeague);
        var texts = new List<string> { p.Title, p.Meta, p.Lead };
        texts.AddRange(p.Contenders.Select(c => c.Blurb));
        texts.AddRange(p.Contenders.Select(c => TournamentPreviewBuilder.MarkLabel(c.Mark, PreviewStage.DistrictLeague)));
        texts.AddRange(p.Contenders.Select(c => TournamentPreviewBuilder.SeedLabel(c.Mark, c.Seed, PreviewStage.DistrictLeague)));
        texts.Add(TournamentPreviewBuilder.ContendersHeading(PreviewStage.DistrictLeague));
        // 出場校すべての校別寸評（パンフレットの登録メンバー表に載る）
        foreach (var s in field)
        {
            var r = TournamentPreviewBuilder.BuildRosterFor(s, field, berths: 2, stage: PreviewStage.DistrictLeague);
            texts.Add(r.TeamBlurb);
            texts.Add(r.SeedLabel);
        }
        foreach (var t in texts)
            foreach (var w in new[] { "優勝", "トーナメント", "シード", "短期決戦", "頂点" })
                Assert.False(t.Contains(w), $"「{w}」が出ている: {t}");
        Assert.Contains("各ブロックの上位2校", p.Lead);
        Assert.Contains("県大会", p.Meta);
    }

    [Fact]
    public void OtherStages_KeepTheirWording()
    {
        // 既存の舞台は従来どおり（勝ち抜き戦の語のまま）
        Assert.Equal("優勝候補", TournamentPreviewBuilder.MarkLabel(ContenderMark.Favorite, PreviewStage.Autumn));
        Assert.Equal("第3シード", TournamentPreviewBuilder.SeedLabel(ContenderMark.Contender, 3, PreviewStage.Autumn));
        Assert.Equal("ノーシード", TournamentPreviewBuilder.SeedLabel(ContenderMark.DarkHorse, 7, PreviewStage.Autumn));
        Assert.Equal("優勝争いの構図", TournamentPreviewBuilder.ContendersHeading(PreviewStage.SummerPrefecture));
    }
}

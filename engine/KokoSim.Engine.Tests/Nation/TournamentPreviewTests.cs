using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
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

    /// <summary>
    /// 甲子園・センバツは49代表/32校が多数の県から集まるため、校名（生成物で一意の保証が無い）が
    /// 別の県の校と偶然一致することがある。以前は byName を素朴な ToDictionary で組んでいたため
    /// 同名の2校目で ArgumentException（重複キー）が飛び、展望画面ごと開けなくなっていた
    /// （2026-08-22 実機検証で発見。KoshienFlowCheck 系の既存ハーネスは展望を一度も呼んでおらず未発見だった）。
    /// </summary>
    [Fact]
    public void Build_DoesNotThrow_WhenTwoEntrantsShareTheSameName()
    {
        var field = new List<School>
        {
            Sch(1, 88, name: "帝都産大付属高校"),
            Sch(2, 80, name: "帝都産大付属高校"),   // 別の県の同名校（衝突）
            Sch(3, 70),
            Sch(4, 60),
        };

        var p = TournamentPreviewBuilder.Build("春季テスト大会", field, berths: 1, "");

        Assert.NotEmpty(p.Contenders);
        // 同名校が居ても落ちない＝先勝ちで畳まれ、両方の格付け行が消えずに残る。
        Assert.Equal(p.Contenders.Count, p.NotablePlayers.Count);
        Assert.Equal(p.Contenders.Count, p.Rosters.Count);
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
        foreach (var n in p.NotablePlayers.Where(x => x.PitcherLed))
            Assert.Contains("最速", n.StatLine);
    }

    [Fact]
    public void NotablePlayer_IsAce_WhenPitchingLeadsTheTeam()
    {
        // 豪腕商業（AceDependent）は投手力が打力を上回る＝エースが看板になり背番号1で出る。
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会");
        var ace = p.NotablePlayers.FirstOrDefault(n => n.SchoolName == "豪腕商業");

        Assert.NotNull(ace);
        Assert.True(ace!.PitcherLed);
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

    // ===== 注目選手の成績行＝今大会の実測値（issue #634） =====
    //
    // 以前はここで能力から確率的な打率・防御率を合成しており、実際の試合結果とは無関係の数字が
    // 「今大会 防御率1.13」として出ていた（裏試合フルシム #43 が実測を積んでいるのに読んでいなかった）。

    /// <summary>SourceId を焼いた素朴なチーム（豪腕商業＝投手力が上＝エースが看板になる）。</summary>
    private static Team MakeAceTeam()
    {
        var fielders = new[]
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
            FieldPosition.DesignatedHitter,
        };
        var order = fielders
            .Select((p, i) => new Player { Position = p, Name = $"{p}先発", UniformNumber = i + 2, SourceId = i + 2 })
            .ToList();
        var pitcher = new Player
        {
            Position = FieldPosition.Pitcher, Name = "エース", UniformNumber = 1, SourceId = 1,
            Pitching = PitcherAttributes.LeagueAverage,
        };
        order[0] = pitcher;
        return new Team
        {
            Name = "豪腕商業", BattingOrder = order, PitcherSlot = 0, StartingPitcher = pitcher,
            Reserves = new List<Player>(),
        };
    }

    private static KokoSim.Engine.Stats.StatBook AceBook()
    {
        var book = new KokoSim.Engine.Stats.StatBook();
        // 27アウト・自責1・奪三振12 の1試合＝防御率1.00。
        book.FoldGame(new GameResult
        {
            AwayName = "豪腕商業", HomeName = "相手", AwayRuns = 3, HomeRuns = 1,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            AwayPitching = new[]
            {
                new PitchingLine("エース", Outs: 27, BattersFaced: 30, Hits: 4, Runs: 1, StrikeOuts: 12,
                    Walks: 1, Pitches: 110, SourceId: 1, EarnedRuns: 1),
            },
        }, managerIsAway: true);
        return book;
    }

    [Fact]
    public void NotableStatLine_UsesMeasuredStats_WhenTheSchoolHasPlayed()
    {
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会",
            teamProvider: (school, year) =>
                school.Name == "豪腕商業" ? MakeAceTeam() : StrengthTeamFactory.ForSchool(school, year),
            statsForSchool: id => id == 3 ? AceBook() : null);

        var ace = p.NotablePlayers.First(n => n.SchoolName == "豪腕商業");
        Assert.True(ace.PitcherLed);
        Assert.Contains("防御率1.00", ace.StatLine);   // 実測（27アウト・自責1）
        Assert.Contains("12奪三振", ace.StatLine);
    }

    [Fact]
    public void NotableStatLine_SaysNotPlayedYet_WhenTheSchoolHasNoGames()
    {
        // 帳簿を渡さない＝開幕前。合成した見込み値で埋め戻さず「まだしていない」と書く。
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会",
            teamProvider: (school, year) =>
                school.Name == "豪腕商業" ? MakeAceTeam() : StrengthTeamFactory.ForSchool(school, year));

        var ace = p.NotablePlayers.First(n => n.SchoolName == "豪腕商業");
        Assert.Contains("最速", ace.StatLine);          // 能力（真値）は今までどおり出る
        Assert.Contains("今大会 未登板", ace.StatLine);
        Assert.DoesNotContain("防御率", ace.StatLine);
    }

    [Fact]
    public void RosterMembers_CarryTheSourceId_ForStatJoin()
    {
        // 成績の帰属キーを持つのは永続ロスター（#80）＝実戦・裏試合フルシムと同一ソース。
        // 旧 StrengthTeamFactory の使い捨て生成は SourceId を持たない＝成績も積まれない（join も空振りで正しい）。
        var rosters = new KokoSim.Engine.Nation.Roster.NationRosters(
            new KokoSim.Engine.Nation.Roster.AiRosterDeps());
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会",
            teamProvider: (school, year) => rosters.TeamFor(school, year));

        // 校別詳細が (校ID, 選手ID) で今大会成績を join するための帰属キー。
        foreach (var r in p.Rosters)
            Assert.All(r.Members, m => Assert.True(m.SourceId > 0, $"{r.SchoolName} の {m.Name} に SourceId が無い"));
    }

    // ===== DHスロットの登録メンバー表記（enum由来の「指」, issue #70） =====

    private static Team MakeDhTeam()
    {
        var fielders = new[]
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };
        var order = fielders.Select((p, i) => new Player { Position = p, Name = $"{p}先発", UniformNumber = i + 2 })
            .ToList();
        order.Add(new Player { Position = FieldPosition.Catcher, Name = "DH打者", UniformNumber = 10 });
        var pitcher = new Player
        {
            Position = FieldPosition.Pitcher, Name = "先発P", UniformNumber = 1,
            Pitching = PitcherAttributes.LeagueAverage,
        };
        return new Team
        {
            Name = "強打学園", BattingOrder = order, DhSlot = 8, StartingPitcher = pitcher,
            Reserves = new List<Player> { pitcher },
        };
    }

    [Fact]
    public void Rosters_DhSlot_ShowsDesignatedHitterLabel_NotRealPosition()
    {
        var dhTeam = MakeDhTeam();
        var p = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会",
            teamProvider: (school, year) => school.Name == "強打学園" ? dhTeam : StrengthTeamFactory.ForSchool(school, year));

        var roster = p.Rosters.First(r => r.SchoolName == "強打学園");
        var dhMember = roster.Members.First(m => m.Name == "DH打者");
        Assert.Equal("指", dhMember.PositionLabel);
        // 他の野手は実守備位置のまま。
        Assert.Contains(roster.Members, m => m.Name == "Catcher先発" && m.PositionLabel == "捕");
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
            var actualMembers = actual.BattingOrder.Concat(actual.Reserves)
                .Select(x => (x.Name, x.UniformNumber)).OrderBy(x => x.UniformNumber).ToList();

            Assert.Equal(actualMembers, roster.Members.Select(m => (m.Name, m.UniformNumber)).ToList());
        }

        // 注目選手も実ラインナップに実在する。
        foreach (var n in p.NotablePlayers)
        {
            var school = field.First(s => s.Name == n.SchoolName);
            var actual = StrengthTeamFactory.ForSchool(school, year);
            Assert.Contains(actual.BattingOrder.Concat(actual.Reserves),
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

    /// <summary>
    /// 甲子園は「次のステージ」が無い＝進出枠の言い回しを出さない（設計書06 §3.5b）。
    /// 秋・夏の県大会は従来どおり枠を出す。
    /// </summary>
    [Fact]
    public void Lead_And_Meta_AdaptToTheStage()
    {
        var koshien = TournamentPreviewBuilder.Build(
            "2028年 全国高等学校野球選手権大会", Field(), berths: 1, nextStageName: "",
            stage: PreviewStage.SummerKoshien);
        Assert.Contains("代表", koshien.Meta);
        Assert.DoesNotContain("上位", koshien.Meta);
        Assert.Contains("頂点に立つのは一校だけ", koshien.Lead);
        Assert.DoesNotContain("新チーム始動の秋", koshien.Lead);

        var summer = TournamentPreviewBuilder.Build(
            "2028年 選手権神奈川大会", Field(), berths: 1, nextStageName: "甲子園",
            stage: PreviewStage.SummerPrefecture);
        Assert.Contains("最後の夏", summer.Lead);
        Assert.Contains("甲子園", summer.Meta);

        // 既定（秋）は従来の文言のまま＝旧来の呼び出しは1文字も変わらない。
        var autumn = TournamentPreviewBuilder.Build("秋季テスト県大会", Field(), berths: 2, "地区大会");
        Assert.Contains("新チーム始動の秋", autumn.Lead);
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

    // ===== 任意校ロスターのオンデマンド生成（issue #189） =====

    /// <summary>
    /// 格付け（◎○▲）が付かない無印校でも、クリックされた校のロスター・寸評をオンデマンドで組める。
    /// 従来の Build().Rosters は格付け校ぶんしか作らないため、樹形図のどの校名をクリックしても
    /// 詳細を出せるようにする窓口が本メソッド。
    /// </summary>
    [Fact]
    public void BuildRosterFor_UnmarkedSchool_ReturnsFullRosterAndBlurb()
    {
        var field = Field();
        var plain = field.First(s => s.Name == "県立B");   // 無印（◎○▲どれにも入らない下位校）
        var baseline = TournamentPreviewBuilder.Build("秋季テスト県大会", field, berths: 2, "地区大会");
        Assert.DoesNotContain(baseline.Contenders, c => c.Name == "県立B");   // 前提: 無印であること

        var roster = TournamentPreviewBuilder.BuildRosterFor(plain, field, berths: 2);

        Assert.Equal("県立B", roster.SchoolName);
        Assert.Equal(20, roster.Members.Count);
        Assert.False(string.IsNullOrWhiteSpace(roster.TeamBlurb));
        Assert.False(string.IsNullOrWhiteSpace(roster.SeedLabel));
    }

    /// <summary>任意校ロスターも決定論（同入力→同出力）を維持する。</summary>
    [Fact]
    public void BuildRosterFor_IsDeterministic()
    {
        var field = Field();
        var plain = field.First(s => s.Name == "県立B");

        var a = TournamentPreviewBuilder.BuildRosterFor(plain, field, berths: 2, yearIndex: 3);
        var b = TournamentPreviewBuilder.BuildRosterFor(plain, field, berths: 2, yearIndex: 3);

        Assert.Equal(a.TeamBlurb, b.TeamBlurb);
        Assert.Equal(a.Members.Select(m => (m.Name, m.UniformNumber)), b.Members.Select(m => (m.Name, m.UniformNumber)));
    }

    /// <summary>存在しない校（entrants に含まれない）を渡すと明示的に失敗する。</summary>
    [Fact]
    public void BuildRosterFor_SchoolNotInEntrants_Throws()
    {
        var field = Field();
        var outsider = Sch(999, 50, name: "対象外高校");
        Assert.Throws<System.ArgumentException>(() => TournamentPreviewBuilder.BuildRosterFor(outsider, field, berths: 2));
    }

    /// <summary>
    /// 寸評の語彙バリエーション（issue #189「なるべく多い語彙で」）。同じ格付け・校風の校が並んでも
    /// 文言が使い回しにならないことを、多数の無印校の寸評が複数種類に分散することで確認する。
    /// </summary>
    [Fact]
    public void Blurb_Vocabulary_VariesAcrossSchools()
    {
        var many = Enumerable.Range(1, 40)
            .Select(id => Sch(id, 45, SchoolStyle.Standard, $"分散{id}高校"))
            .ToList();

        // 無印カテゴリの4テンプレートそれぞれに固有の一節（テンプレ選択の判別マーカー）。
        var markers = new[] { "戦力評価", "組み合わせ次第", "堅実な戦い方が持ち味", "克服できれば" };

        var usedTemplates = many
            .Select(s => TournamentPreviewBuilder.BuildRosterFor(s, many, berths: 2).TeamBlurb)
            .Select(blurb => markers.FirstOrDefault(blurb.Contains))
            .Where(m => m != null)
            .Distinct()
            .Count();

        Assert.True(usedTemplates > 1, "同一格付け・校風でも寸評テンプレートが複数種類に分散するはず");
    }
}

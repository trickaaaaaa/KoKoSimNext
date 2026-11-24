using System;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season.Events;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// データ・シナリオの読み込みで、不正な値や宣言が黙って別の値に化けないこと（issue #1253）。
/// 不正値は例外＝読み込み時に気づける。宣言したものは宣言どおり効く。
/// </summary>
public sealed class LoadStrictnessTests
{
    private static ScenarioDefinition Def(Func<ScenarioDefinition, ScenarioDefinition>? edit = null)
    {
        var d = new ScenarioDefinition { Id = "t", Away = "AI:tier=C", Home = "AI:tier=C" };
        return edit is null ? d : edit(d);
    }

    // ---- 1. シナリオ ----

    [Fact]
    public void Scenario_DhFalse_RemovesDhFromAPlayerTeamThatHasIt()
    {
        var withDh = ScenarioBuilder.Build(Def(d => d with { Dh = true }), new GameContext(), 5UL);
        Assert.True(withDh.Home.UsesDh);

        var built = ScenarioBuilder.Build(
            Def(d => d with { Home = "player", Dh = false }), new GameContext(), 5UL, playerTeam: withDh.Home);

        Assert.False(built.Home.UsesDh);
        Assert.Null(built.Home.StartingPitcher);
        Assert.Equal(withDh.Home.StartingPitcher, built.Home.BattingOrder[built.Home.PitcherSlot]);
        Assert.Equal(9, built.Home.BattingOrder.Count);
    }

    [Fact]
    public void Scenario_DhFalse_PlaysAFullGame()
    {
        var withDh = ScenarioBuilder.Build(Def(d => d with { Dh = true }), new GameContext(), 5UL);
        var built = ScenarioBuilder.Build(
            Def(d => d with { Home = "player", Dh = false }), new GameContext(), 5UL, playerTeam: withDh.Home);
        var r = GameEngine.Play(built.Away, built.Home, built.Ctx, new Xoshiro256Random(built.Seed));
        Assert.True(r.TotalPitches > 0);
    }

    [Fact]
    public void Scenario_DeclaredSeed_FixesTheTeamsRegardlessOfCallerSeed()
    {
        var def = Def(d => d with { Seed = 777UL });
        var a = ScenarioBuilder.Build(def, new GameContext(), 1UL);
        var b = ScenarioBuilder.Build(def, new GameContext(), 2UL);
        Assert.Equal(777UL, a.Seed);
        Assert.Equal(777UL, b.Seed);
        Assert.Equal(a.Away.BattingOrder.Select(p => p.Name), b.Away.BattingOrder.Select(p => p.Name));
        Assert.Equal(a.Home.BattingOrder.Select(p => p.Name), b.Home.BattingOrder.Select(p => p.Name));
    }

    [Fact]
    public void Scenario_NoSeed_UsesTheCallerSeedForTeamsAndGame()
    {
        var def = Def();
        var a = ScenarioBuilder.Build(def, new GameContext(), 1UL);
        var b = ScenarioBuilder.Build(def, new GameContext(), 2UL);
        Assert.Equal(1UL, a.Seed);
        Assert.NotEqual(a.Away.BattingOrder.Select(p => p.Name), b.Away.BattingOrder.Select(p => p.Name));
    }

    [Theory]
    [InlineData(1, false, 5, 3)]   // 1回から始めても得点がラインスコアから消えない（裏から）
    [InlineData(1, true, 2, 1)]    // 1回表から
    [InlineData(3, true, 2, 1)]    // 3回表から（従来どおり）
    public void Scenario_SeededScore_MatchesLineScoreTotals(int inning, bool top, int away, int home)
    {
        var def = Def(d => d with { Inning = inning, Top = top, AwayScore = away, HomeScore = home });
        var built = ScenarioBuilder.Build(def, new GameContext(), 9UL);
        var prog = GameEngine.NewProgress(built.Away, built.Home, built.Ctx, new Xoshiro256Random(built.Seed),
            scenarioStart: built.Start);
        foreach (var _ in GameEngine.Steps(prog)) { }
        var r = GameEngine.BuildResult(prog);

        Assert.Equal(r.AwayRuns, r.AwayLineScore.Sum());
        if (r.HomeLineScore.Count > 0 || r.HomeRuns == 0) Assert.Equal(r.HomeRuns, r.HomeLineScore.Sum());
    }

    [Theory]
    [InlineData(9, false, 2, 3)]    // 9回裏、後攻リードで始める＝裏は行われない
    [InlineData(10, true, 2, 3)]    // 延長10回表で点差がある＝9回裏で決着済み
    [InlineData(5, false, 0, 10)]   // 5回裏、コールド差で後攻リード
    public void Scenario_AlreadyFinishedAtStart_Throws(int inning, bool top, int away, int home)
    {
        var def = Def(d => d with { Inning = inning, Top = top, AwayScore = away, HomeScore = home });
        var ctx = new GameContext { MercyRuleEnabled = true };
        var ex = Assert.Throws<ArgumentException>(() => ScenarioBuilder.Build(def, ctx, 1UL));
        Assert.Contains("終わって", ex.Message);
    }

    [Theory]
    [InlineData(9, false, 3, 3)]    // 9回裏同点＝サヨナラの場面
    [InlineData(9, false, 4, 3)]    // 9回裏ビハインド
    [InlineData(9, true, 2, 3)]     // 9回表（後攻リード）はこれから表
    [InlineData(10, true, 3, 3)]    // 延長10回表・同点
    public void Scenario_PlayableStarts_AreAccepted(int inning, bool top, int away, int home)
    {
        var def = Def(d => d with { Inning = inning, Top = top, AwayScore = away, HomeScore = home });
        ScenarioBuilder.Build(def, new GameContext(), 1UL);
    }

    // ---- 2・数値. イベント表 ----

    private const string EventHead = "events:\n  - id: e1\n";

    [Fact]
    public void Event_UnknownKind_Throws()
    {
        Assert.Throws<FormatException>(() => EventTableParser.Parse(EventHead + "    kind: Sytem\n    text: a\n"));
    }

    [Theory]
    [InlineData("", EventKind.Random)]
    [InlineData("    kind: Random\n", EventKind.Random)]
    [InlineData("    kind: System\n", EventKind.System)]
    public void Event_KnownOrOmittedKind_Parses(string line, EventKind expected)
    {
        var ev = EventTableParser.Parse(EventHead + line + "    text: a\n").Single();
        Assert.Equal(expected, ev.Kind);
    }

    [Theory]
    [InlineData("weight: 2,0")]
    [InlineData("cooldown_weeks: four")]
    [InlineData("calendar_week: 1o")]
    public void Event_UnreadableNumber_Throws(string line)
    {
        Assert.Throws<FormatException>(() => EventTableParser.Parse(EventHead + "    " + line + "\n    text: a\n"));
    }

    [Theory]
    [InlineData("funds: 1.2.3")]
    [InlineData("training_mult: x")]
    [InlineData("training_weeks: 2.5")]
    [InlineData("pressure_next_match: -")]
    public void Event_UnreadableEffectNumber_Throws(string line)
    {
        var yaml = EventHead + "    text: a\n    effects:\n      " + line + "\n";
        Assert.Throws<FormatException>(() => EventTableParser.Parse(yaml));
    }

    [Fact]
    public void Event_ValidNumbers_StillParse()
    {
        var yaml = EventHead + "    weight: 2.5   # 重み\n    cooldown_weeks: 6\n    calendar_week: 12\n    text: a\n"
                   + "    effects:\n      funds: -30\n      training_mult: 1.5\n      training_weeks: 2\n";
        var ev = EventTableParser.Parse(yaml).Single();
        Assert.Equal(2.5, ev.Weight);
        Assert.Equal(6, ev.CooldownWeeks);
        Assert.Equal(12, ev.CalendarWeek);
        Assert.Equal(-30, ev.Effect.Funds);
    }

    [Fact]
    public void Event_HashInsideSingleQuotesOrGluedToValue_IsNotAComment()
    {
        var yaml = EventHead + "    text: 'A #1 team'\n    scene: ground\n";
        Assert.Equal("A #1 team", EventTableParser.Parse(yaml).Single().Text);
        var glued = EventHead + "    text: a#b\n";
        Assert.Equal("a#b", EventTableParser.Parse(glued).Single().Text);
    }

    // ---- 3. スキルカタログ ----

    [Fact]
    public void SkillCatalog_NumericId_IsNotAccepted()
    {
        var yaml = "skills:\n  - id: 3\n    category: batting\n    name: x\n"
                   + "  - id: ClutchHitter\n    category: batting\n    name: y\n";
        var entries = SkillCatalogParser.Parse(yaml);
        Assert.Equal(new[] { Skill.ClutchHitter }, entries.Select(e => e.Id));
    }

    // ---- 4. チーム色 ----

    private static string Palette(string extra)
        => "palettes:\n  - { id: p1, base: \"#EDEBE0\", team: \"#293356\"" + extra + " }\n";

    [Theory]
    [InlineData(", visor: \"#ZZZZZZ\"")]
    [InlineData(", stripe: { color: \"nope\" }")]
    [InlineData(", stripe: { freq: abc }")]
    [InlineData(", stripe: { freq: 0 }")]
    public void TeamColor_BadVisorOrStripe_Throws(string extra)
    {
        Assert.Throws<FormatException>(() => TeamColorParser.Parse(Palette(extra)));
    }

    [Fact]
    public void TeamColor_GoodVisorAndStripe_Parse()
    {
        var p = TeamColorParser.Parse(Palette(", visor: \"#102030\", stripe: { color: \"#405060\", freq: 30 }"));
        Assert.NotNull(p);
    }

    // ---- 5. 県フォーマットの stages ----

    [Fact]
    public void PrefFormat_NestedListInsideStage_DoesNotCloseTheStage()
    {
        const string yaml = @"pref: testpref
district_assignment: geographic
stages:
  - name: 地区予選
    type: group_split
    note:
      - 入れ子のリスト
      - もうひとつ
    grouping: geographic
    groups: 6
    advance_per_group: 2
";
        var rule = PrefFormatDistrictParser.ParseRule(yaml);
        Assert.Equal(6, rule.Districts);
        Assert.Equal(2, rule.AdvancePerDistrict);
    }

    [Fact]
    public void PrefFormat_TwoStages_StillSplit()
    {
        const string yaml = @"pref: testpref
district_assignment: geographic
stages:
  - name: 県大会
    type: knockout
  - name: 地区予選
    type: group_split
    grouping: geographic
    groups: 4
";
        Assert.Equal(4, PrefFormatDistrictParser.ParseRule(yaml).Districts);
    }

    [Fact]
    public void PrefFormat_HashInsideQuotes_IsKept()
    {
        const string yaml = "pref: testpref\ndistrict_assignment: geographic\ndistricts: [\"A#1\", \"B\"]\n"
                            + "stages:\n  - type: group_split\n    grouping: geographic\n    groups: 2\n";
        var rule = PrefFormatDistrictParser.ParseRule(yaml);
        Assert.Equal("A#1", rule.NameOf(0));
    }

    // ---- 6. SkillSet の等価 ----

    [Fact]
    public void SkillSet_EqualityIsByContent()
    {
        var a = new SkillSet(new[] { Skill.ClutchHitter, Skill.Durable }, new[] { Skill.Lazy });
        var b = new SkillSet(new[] { Skill.Durable, Skill.ClutchHitter }, new[] { Skill.Lazy });
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new SkillSet(new[] { Skill.ClutchHitter }, new[] { Skill.Lazy }));
        Assert.NotEqual(a, new SkillSet(new[] { Skill.ClutchHitter, Skill.Durable, Skill.Lazy }));
        Assert.Equal(SkillSet.Empty, new SkillSet());
    }

    // ---- 7. 全国成績の読み取りは畳み込みから切り離される ----

    [Fact]
    public void NationStats_ReadersGetASnapshot_NotTheLiveBook()
    {
        var stats = new NationTournamentStats();
        stats.GetOrCreateBook(7).GetOrCreate(1);
        stats.GetOrCreateCareerBook(7).GetOrCreate(1);
        var book = stats.ForSchool(7)!;
        var career = stats.CareerForSchool(7)!;

        stats.GetOrCreateBook(7).GetOrCreate(2);
        stats.GetOrCreateCareerBook(7).GetOrCreate(2);

        Assert.Single(book.Players);
        Assert.Single(career.Players);
        Assert.Equal(2, stats.ForSchool(7)!.Players.Count);
        Assert.Equal(2, stats.CareerForSchool(7)!.Players.Count);
    }
}

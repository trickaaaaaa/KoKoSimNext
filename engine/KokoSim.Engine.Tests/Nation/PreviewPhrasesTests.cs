using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会展望の文言（issue #784）。寸評・注目選手の紹介を data/preview-phrases.yaml の部品から組み、
/// ①同じ大会の中で同じ寸評文を2校に出さない ②全出場校に注目選手が1人いる
/// ③弱小校の選手に強豪向けの最上級表現を使わない ④同じ出場校・同じ年なら同じ文、を確かめる。
/// </summary>
public sealed class PreviewPhrasesTests
{
    private static readonly PreviewPhrases Phrases = PreviewPhrasesParser.Parse(
        File.ReadAllText(Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("preview-phrases.yaml")));

    /// <summary>出場200校の県大会（強さ12〜95・校風6種が混ざる）。</summary>
    private static List<School> Field200()
        => Enumerable.Range(0, 200).Select(i => new School
        {
            Id = 1000 + i, Name = $"出場{i + 1}高校", PrefectureId = 0,
            Strength = 95 - i * 83.0 / 199 + (i % 7) * 0.01,
            Style = (SchoolStyle)(i * 5 % 6),
        }).ToList();

    private static readonly string[] Bands = { "fav", "cont", "dark", "upper", "middle", "lower", "bottom" };
    private static readonly string[] Profiles =
        { "balanced.strong", "balanced.weak", "spike.bat", "spike.pitch", "spike.def", "hole.bat", "hole.pitch", "hole.def" };
    private static readonly string[] Styles = { "smallball", "power", "defense", "total", "ace", "standard" };
    private static readonly string[] Traits =
        { "power", "contact", "speed", "arm", "eye", "glove", "ace.power", "ace.finesse", "ace.soft", "ace.balanced",
          "ace.flamethrower", "ace.pinpoint", "ace.artist" };   // 尖った型（issue #908）
    private static readonly string[] Levels = { "star", "solid", "modest" };

    /// <summary>組み立てが参照しうる置き場の全部。</summary>
    private static IEnumerable<string> AllKeys()
    {
        foreach (var b in Bands) yield return "school.open." + b;
        foreach (var b in new[] { "fav", "cont", "dark" }) yield return "school.open.league." + b;
        foreach (var p in Profiles) yield return "school.body." + p;
        foreach (var s in Styles) { yield return $"school.close.{s}.strong"; yield return $"school.close.{s}.weak"; }
        yield return "player.intro";
        foreach (var t in Traits)
        {
            foreach (var l in Levels) yield return $"player.head.{t}.{l}";
            yield return "player.body." + t;
        }
        for (var g = 1; g <= 3; g++) yield return "player.tail.g" + g;
    }

    [Fact]
    public void Yaml_HasEveryKeyTheWriterUses_AndNoStrayKeys()
    {
        var keys = Phrases.Keys.ToHashSet();
        foreach (var k in AllKeys()) Assert.True(keys.Contains(k), $"data/preview-phrases.yaml に {k} が無い");
        // 綴り違いの置き場は黙って既定文に落ちる＝余計な名前が無いことも固定する
        Assert.Empty(keys.Except(AllKeys()));
    }

    /// <summary>
    /// 被らない保証は「置き場の本数の積 ≥ 同じ組に入る数」。寸評は組ごとの通し番号なので積≥200で十分、
    /// 選手は出場校全体の順位で番号を振るので、積が出場校数（全国生成で最大287校）以上でなければならない。
    /// </summary>
    [Fact]
    public void Yaml_PoolSizes_GiveEnoughCombinations()
    {
        int N(string key) => Phrases.Pool(key).Count;
        foreach (var b in Bands)
        foreach (var p in Profiles)
        foreach (var s in Styles)
        {
            var close = $"school.close.{s}." + (b is "middle" or "lower" or "bottom" ? "weak" : "strong");
            Assert.True(N("school.open." + b) * N("school.body." + p) * N(close) >= 200, $"{b}/{p}/{s}");
        }
        // 全国生成で最大の県（東京）は287校。余裕を見て320
        foreach (var t in Traits)
        foreach (var l in Levels)
        for (var g = 1; g <= 3; g++)
            Assert.True(N($"player.head.{t}.{l}") * N("player.body." + t) * N("player.tail.g" + g) >= 320, $"{t}/{l}/g{g}");
    }

    /// <summary>
    /// 同じ文が複数の置き場に入っていると、別の組の学校で文が一致しうる。部品の文は全体で一意にする
    /// （勝ち抜き戦とリーグ戦の書き出しは同じ大会に並ばないので、その組だけは重なってよい）。
    /// </summary>
    [Fact]
    public void Yaml_PhrasesAreUniqueAcrossPools()
    {
        var seen = new Dictionary<string, string>();
        foreach (var k in Phrases.Keys.Where(k => !k.StartsWith("school.open.league.")))
        foreach (var line in Phrases.Pool(k))
        {
            Assert.False(seen.TryGetValue(line, out var other), $"「{line}」が {other} と {k} の両方にある");
            seen[line] = k;
        }
    }

    /// <summary>
    /// 「{area}屈指」などの最上級表現は star（強豪の看板）の見出しだけ。勝ち抜き戦の語は ◎○▲ の勝ち抜き戦用の
    /// 書き出しだけ（無印・特徴・締め・選手の部品はリーグ戦でも使う）。守備位置・投手適性の語はどこにも書かない。
    /// </summary>
    [Fact]
    public void Yaml_WordsStayInTheirPlace()
    {
        var superlatives = new[] { "{area}", "屈指", "指折り", "トップクラス" };
        var knockout = new[] { "優勝", "トーナメント", "シード", "短期決戦", "頂点" };
        var positions = new[] { "遊撃", "二遊間", "捕手", "内野手", "外野手", "一塁手", "二塁手", "三塁手",
            "中堅手", "左翼手", "右翼手", "本来は", "投手として" };
        foreach (var k in Phrases.Keys)
        foreach (var line in Phrases.Pool(k))
        {
            if (!(k.StartsWith("player.head.") && k.EndsWith(".star")))
                foreach (var w in superlatives) Assert.False(line.Contains(w), $"{k}:「{line}」に {w}");
            if (k is not ("school.open.fav" or "school.open.cont" or "school.open.dark"))
                foreach (var w in knockout) Assert.False(line.Contains(w), $"{k}:「{line}」に {w}");
            // 甲子園にはシードが無い＝勝ち抜き戦用の書き出しにも書かない
            Assert.False(line.Contains("シード"), $"{k}:「{line}」に シード");
            foreach (var w in positions) Assert.False(line.Contains(w), $"{k}:「{line}」に {w}");
        }
    }

    // ===== 実際に組んだ文 =====

    [Theory]
    [InlineData(PreviewStage.SummerPrefecture)]
    [InlineData(PreviewStage.DistrictLeague)]
    public void SchoolBlurbs_AreAllDifferent_InA200SchoolTournament(PreviewStage stage)
    {
        var field = Field200();
        var p = TournamentPreviewBuilder.Build("夏の県大会", field, berths: 1, "甲子園", yearIndex: 3,
            stage: stage, phrases: Phrases);
        Assert.NotEmpty(p.Contenders);

        var ranked = field.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).ToList();
        var blurbs = PreviewWriterProbe.SchoolBlurbs(ranked, berths: 1, stage, yearIndex: 3, Phrases);
        Assert.Equal(200, blurbs.Count);
        var dup = blurbs.GroupBy(b => b).FirstOrDefault(g => g.Count() > 1);
        Assert.True(dup is null, $"同じ寸評が2校に出た: {dup?.Key}");
        Assert.All(blurbs, b => Assert.DoesNotContain("{", b));
    }

    /// <summary>展望の上段（◎○▲）は、どの学校も違う一文で書き出す（対抗2校が同じ書き出しで並ばない）。</summary>
    [Theory]
    [InlineData(PreviewStage.SummerKoshien)]
    [InlineData(PreviewStage.DistrictLeague)]
    public void Contenders_StartWithDifferentSentences(PreviewStage stage)
    {
        var field = Field200();
        for (var year = 1; year <= 20; year++)
        {
            var p = TournamentPreviewBuilder.Build("t", field, berths: 2, "次", yearIndex: year, stage: stage, phrases: Phrases);
            var heads = p.Contenders.Select(c => c.Blurb.Substring(0, c.Blurb.IndexOf('。') + 1)).ToList();
            Assert.Equal(heads.Count, heads.Distinct().Count());
        }
    }

    /// <summary>全出場校に注目選手が1人いて、紹介文が空でなく、200校の中で被らない。</summary>
    [Fact]
    public void EverySchool_HasANotablePlayer_AndTheirBlurbsDiffer()
    {
        var field = Field200();
        var blurbs = new List<string>();
        foreach (var s in field)
        {
            var r = TournamentPreviewBuilder.BuildRosterFor(s, field, berths: 1, yearIndex: 3,
                stage: PreviewStage.SummerPrefecture, phrases: Phrases);
            Assert.Contains("年の", r.TeamBlurb);   // 導入「注目は2年の山田。」
            var notable = PreviewWriterProbe.NotableBlurb(s, field, berths: 1, PreviewStage.SummerPrefecture, 3, Phrases);
            Assert.False(string.IsNullOrWhiteSpace(notable));
            Assert.EndsWith(notable, r.TeamBlurb);
            blurbs.Add(notable);
        }
        var dup = blurbs.GroupBy(b => b).FirstOrDefault(g => g.Count() > 1);
        Assert.True(dup is null, $"同じ選手紹介が2人に出た: {dup?.Key}");
    }

    /// <summary>
    /// 実際の全国生成で最も出場校の多い県（東京・287校）でも、寸評も注目選手の紹介も被らない。
    /// </summary>
    [Fact]
    public void LargestRealPrefecture_HasNoRepeatedBlurbs()
    {
        var nation = NationGenerator.Generate(
            new SchoolNameVocab(), new NationCoefficients(), new KokoSim.Engine.Core.Xoshiro256Random(2026));
        var field = nation.Prefectures.Select(p => nation.InPrefecture(p.Id).ToList()).OrderByDescending(f => f.Count).First();
        Assert.True(field.Count > 250, $"最大の県が {field.Count} 校");

        var schools = TournamentPreviewBuilder.SchoolBlurbsFor(field, 1, PreviewStage.SummerPrefecture, 2, Phrases).Values.ToList();
        Assert.Equal(schools.Count, schools.Distinct().Count());

        var players = field.Select(s => TournamentPreviewBuilder.NotableBlurbFor(s, field, PreviewStage.SummerPrefecture, 2, Phrases)).ToList();
        var dup = players.GroupBy(b => b).FirstOrDefault(g => g.Count() > 1);
        Assert.True(dup is null, $"同じ選手紹介が2人に出た: {dup?.Key}");
    }

    /// <summary>最弱の学校の注目選手には強豪向けの最上級表現が出ず、最強の学校の看板には出る。</summary>
    [Fact]
    public void NotableWording_MatchesThePlayersLevel()
    {
        var field = Field200();
        var ranked = field.OrderByDescending(s => s.Strength).ToList();
        foreach (var weak in ranked.Skip(170))
        {
            var text = PreviewWriterProbe.NotableBlurb(weak, field, 1, PreviewStage.SummerPrefecture, 3, Phrases);
            foreach (var w in new[] { "県内", "全国", "屈指", "指折り", "トップクラス" })
                Assert.False(text.Contains(w), $"{weak.Name}（強さ{weak.Strength:0}）: 「{text}」に {w}");
        }
        var top = PreviewWriterProbe.NotableBlurb(ranked[0], field, 1, PreviewStage.SummerPrefecture, 3, Phrases);
        Assert.Contains("県内", top);
        // 甲子園の展望では「全国」で書く（県の大会と同じ選手でも舞台で言い方が変わる）
        var koshien = PreviewWriterProbe.NotableBlurb(ranked[0], field, 1, PreviewStage.SummerKoshien, 3, Phrases);
        Assert.DoesNotContain("県内", koshien);
    }

    /// <summary>同じ出場校・同じ年なら同じ文。年が変われば文も変わる。</summary>
    [Fact]
    public void Blurbs_AreDeterministic_AndChangeWithTheYear()
    {
        var field = Field200();
        var ranked = field.OrderByDescending(s => s.Strength).ThenBy(s => s.Id).ToList();
        var a = PreviewWriterProbe.SchoolBlurbs(ranked, 1, PreviewStage.Autumn, 3, Phrases);
        var b = PreviewWriterProbe.SchoolBlurbs(ranked, 1, PreviewStage.Autumn, 3, Phrases);
        var next = PreviewWriterProbe.SchoolBlurbs(ranked, 1, PreviewStage.Autumn, 4, Phrases);
        Assert.Equal(a, b);
        Assert.True(a.Zip(next).Count(x => x.First != x.Second) > 150, "年が変わっても文がほとんど同じ");

        var s = field[57];
        var r1 = TournamentPreviewBuilder.BuildRosterFor(s, field, 1, yearIndex: 3, phrases: Phrases);
        var r2 = TournamentPreviewBuilder.BuildRosterFor(s, field, 1, yearIndex: 3, phrases: Phrases);
        Assert.Equal(r1.TeamBlurb, r2.TeamBlurb);
    }

    /// <summary>
    /// 展望の一覧（Build）と1校詳細（BuildRosterFor）で同じ学校の寸評・注目選手が食い違わない。
    /// 1校詳細の寸評は「一覧の寸評＋導入＋注目選手の紹介」。
    /// </summary>
    [Fact]
    public void ListAndDetail_TellTheSameStory()
    {
        var field = Field200();
        var p = TournamentPreviewBuilder.Build("夏の県大会", field, berths: 1, "甲子園", yearIndex: 3,
            stage: PreviewStage.SummerPrefecture, phrases: Phrases);
        foreach (var c in p.Contenders)
        {
            var school = field.First(s => s.Name == c.Name);
            var detail = TournamentPreviewBuilder.BuildRosterFor(school, field, berths: 1, yearIndex: 3,
                stage: PreviewStage.SummerPrefecture, phrases: Phrases);
            var notable = p.NotablePlayers.First(n => n.SchoolName == c.Name);
            Assert.StartsWith(c.Blurb, detail.TeamBlurb);
            Assert.EndsWith(notable.Blurb, detail.TeamBlurb);
            Assert.Contains(notable.Name.Replace("　", ""), detail.TeamBlurb);
            Assert.Equal(detail, p.Rosters.First(r => r.SchoolName == c.Name), new ExcerptBlurbComparer());
        }
    }

    private sealed class ExcerptBlurbComparer : IEqualityComparer<RosterExcerpt>
    {
        public bool Equals(RosterExcerpt? x, RosterExcerpt? y) => x?.TeamBlurb == y?.TeamBlurb;
        public int GetHashCode(RosterExcerpt obj) => obj.TeamBlurb.GetHashCode();
    }
}

/// <summary>テストから組み立て部を直接呼ぶ窓口（Build と同じ順位・格付けで組む）。</summary>
internal static class PreviewWriterProbe
{
    public static List<string> SchoolBlurbs(
        List<School> ranked, int berths, PreviewStage stage, int yearIndex, PreviewPhrases phrases)
    {
        var map = TournamentPreviewBuilder.SchoolBlurbsFor(ranked, berths, stage, yearIndex, phrases);
        return ranked.Select(s => map[s.Id]).ToList();
    }

    public static string NotableBlurb(
        School school, List<School> field, int berths, PreviewStage stage, int yearIndex, PreviewPhrases phrases)
        => TournamentPreviewBuilder.NotableBlurbFor(school, field, stage, yearIndex, phrases);
}

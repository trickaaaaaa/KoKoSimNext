using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

public sealed class NationTests
{
    private static readonly SchoolNameVocab Vocab = new();
    private static readonly NationCoefficients Coeff = new();

    // --- 生成 ---

    [Fact]
    public void Generate_ProducesAboutFourThousandSchools()
    {
        var nation = NationGenerator.Generate(Vocab, Coeff, new Xoshiro256Random(1));
        Assert.InRange(nation.Schools.Count, 3800, 4200);
        Assert.Equal(47, nation.Prefectures.Count);
    }

    [Fact]
    public void TierFromStrength_MapsBands()
    {
        Assert.Equal(Tier.S, Tiers.FromStrength(95));
        Assert.Equal(Tier.C, Tiers.FromStrength(65));
        Assert.Equal(Tier.G, Tiers.FromStrength(20));
    }

    [Fact]
    public void NameGenerator_ProducesNonEmptyNames()
    {
        var rng = new Xoshiro256Random(3);
        for (var i = 0; i < 50; i++)
        {
            var (name, _) = SchoolNameGenerator.Generate(Vocab, rng);
            Assert.False(string.IsNullOrEmpty(name));
            Assert.EndsWith("高校", name);
        }
    }

    // --- 集計マッチのキャリブレーション（フルエンジンとの整合, 設計書05 §1.4） ---

    [Theory]
    [Trait("Category", "Heavy")] // フルエンジンで多数試合を回すキャリブレーション照合（約40秒）
    [InlineData(55, 45)]  // 差10 → エンジン実測 ≈0.92
    [InlineData(60, 50)]
    public void AggregateModel_MatchesGameEngineWinRate(double sa, double sb)
    {
        var gameCtx = new GameContext();
        var root = new Xoshiro256Random(1);
        const int n = 300;
        var engineWins = 0;
        for (var i = 0; i < n; i++)
        {
            var g = root.Fork((ulong)i);
            var a = StrengthTeamFactory.Create(sa, "A", g);
            var b = StrengthTeamFactory.Create(sb, "B", g);
            var r = GameEngine.Play(a, b, gameCtx, g);
            if (r.AwayRuns > r.HomeRuns) engineWins++;
            else if (r.Tied && g.NextDouble() < 0.5) engineWins++;
        }
        var engineRate = (double)engineWins / n;
        var modelRate = AggregateMatch.WinProbability(sa, sb, Coeff);

        Assert.InRange(modelRate, engineRate - 0.08, engineRate + 0.08);
    }

    // --- 裏試合3層のキャリブレーション（設計書05 §1.4 / 11 §5） ---
    // 集計モデル（強さ差）が、フル采配（AiTacticsBrain＝三層①能力値②ティア③校風）の試合と統計整合するか。
    // 実測所見: 三層の純W/L効果は許容帯内（ティアは強さに内包、采配能力≈0、校風＝小技は強さ優位を数%削るのみ）。
    // → 集計モデルに W/L 補正項は不要。強さ差モデルのまま裏表が整合することを固定する（no-fudge, 難易度補正なし）。

    [Theory]
    [Trait("Category", "Heavy")]
    [InlineData(55, 55)]  // 互角＋強い側が机動力フル采配 → 集計0.5にフルが追随
    [InlineData(57, 49)]  // 差8＋強い側が采配 → 集計 Logistic(8/scale)≈0.88 にフルが追随
    public void AggregateModel_StaysCalibrated_WhenStrongerSideUsesFullTactics(double sa, double sb)
    {
        var gameCtx = new GameContext();
        var root = new Xoshiro256Random(1);
        const int n = 300;
        // 強い側は機動力・采配能力80の敵AIブレインでフル采配（小技を最も使う＝集計との乖離が最大の校風）。
        var brainSchool = new School
        {
            Id = 1, Name = "AI", PrefectureId = 0, Strength = sa,
            TacticalSense = 80, Style = SchoolStyle.SmallBall,
        };
        var engineWins = 0;
        for (var i = 0; i < n; i++)
        {
            var g = root.Fork((ulong)i);
            var a = StrengthTeamFactory.Create(sa, "A", g) with { Tactics = EnemyAiFactory.BrainFor(brainSchool) };
            var b = StrengthTeamFactory.Create(sb, "B", g);
            var r = GameEngine.Play(a, b, gameCtx, g);
            if (r.AwayRuns > r.HomeRuns) engineWins++;
            else if (r.Tied && g.NextDouble() < 0.5) engineWins++;
        }
        var engineRate = (double)engineWins / n;
        var modelRate = AggregateMatch.WinProbability(sa, sb, Coeff);

        // フル采配を入れても、集計モデル（強さ差のみ）は実分布に許容帯内で追随する。
        Assert.InRange(modelRate, engineRate - 0.08, engineRate + 0.08);
    }

    [Fact]
    public void Tournament_StrongerFieldProducesStrongChampion()
    {
        var rng = new Xoshiro256Random(5);
        var schools = new List<School>();
        for (var i = 0; i < 64; i++)
        {
            schools.Add(new School
            {
                Id = i, Name = $"S{i}", PrefectureId = 0,
                Strength = 30 + i, // 明確な強さ勾配
            });
        }
        // 多数回で優勝校の平均強さが上位帯に寄る。
        var championStrengthSum = 0.0;
        const int trials = 200;
        for (var t = 0; t < trials; t++)
        {
            var res = TournamentEngine.Run(schools, Coeff, rng);
            championStrengthSum += res.Champion.Strength;
        }
        var avgChampStrength = championStrengthSum / trials;
        // 最強93付近が多く勝つはず（下位ではない）。
        Assert.True(avgChampStrength > 80, $"優勝校平均強さ {avgChampStrength:F1}");
    }

    // --- 10年運営 DoD: 勢力図変動＋全国統計安定 ---

    [Fact]
    public void TenYearNation_PowerMapChanges_AndStatsStable()
    {
        var history = NationEngine.Run(12, Vocab, Coeff, new Xoshiro256Random(42));

        // 勢力図変動: 12年で複数の異なる優勝校。
        var distinctChampions = history.Years.Select(y => y.ChampionId).Distinct().Count();
        Assert.True(distinctChampions >= 4, $"優勝校の多様性 {distinctChampions}");

        // 毎年ティア変動が起きている（古豪凋落・新興台頭）。
        Assert.True(history.Years.Skip(1).All(y => y.TierChangesFromLastYear > 0));

        // 全国統計安定: 平均強さが全年で妥当な帯に収まる（暴走・崩壊しない）。
        Assert.True(history.Years.All(y => y.AverageStrength is > 38 and < 50),
            "平均強さが安定帯を外れた");

        // Sティアが毎年 0〜15 程度で維持（インフレ・消滅しない）。
        Assert.True(history.Years.All(y => y.TierCounts[Tier.S] <= 15),
            "Sティアがインフレした");
        // 強豪が枯渇しない（A以上が常に存在）。
        Assert.True(history.Years.All(y => y.TierCounts[Tier.A] + y.TierCounts[Tier.S] >= 3),
            "強豪が枯渇した");
    }

    [Fact]
    public void LongHorizonNation_AverageStrength_DoesNotDrift()
    {
        // 30年運営で平均強さが一定帯に「戻って留まる」（設計書05 §2.3の安定化）。
        // 回帰前は fame の固定50基準により平均強さが単調に痩せていた（45→…→36へ沈む）。
        // 母集団平均名声を基準にした後は、初期45付近から平均回帰目標(44)へ収束しそこで安定する。
        var history = NationEngine.Run(30, Vocab, Coeff, new Xoshiro256Random(42));
        var avgs = history.Years.Select(y => y.AverageStrength).ToList();

        // 全年で安定帯に収まる。
        Assert.All(avgs, a => Assert.InRange(a, 43.0, 47.0));

        // 後半10年は均衡（平均回帰目標44＋勝利ボーナスで45強）近傍で落ち着く（単調な痩せが起きていない）。
        var tail = avgs.Skip(20).ToList();
        Assert.All(tail, a => Assert.InRange(a, 44.5, 46.0));

        // 終盤が序盤より2以上低い、という systematic な下押しが無い。
        Assert.True(avgs[^1] > avgs[0] - 2.0,
            $"平均強さが単調ドリフト: 初年 {avgs[0]:F1} → 最終 {avgs[^1]:F1}");
    }

    [Fact]
    public void Nation_IsDeterministic()
    {
        var a = NationEngine.Run(8, Vocab, Coeff, new Xoshiro256Random(7));
        var b = NationEngine.Run(8, Vocab, Coeff, new Xoshiro256Random(7));
        for (var i = 0; i < a.Years.Count; i++)
        {
            Assert.Equal(a.Years[i].ChampionId, b.Years[i].ChampionId);
            Assert.Equal(a.Years[i].AverageStrength, b.Years[i].AverageStrength, 6);
        }
    }

    [Fact]
    public void Generate_SchoolNames_AreUniqueWithinEachPrefecture()
    {
        // 校名生成時に県内ユニーク化（同名校が同じ大会・プレビューに並ばない）。
        var nation = NationGenerator.Generate(Vocab, Coeff, new Xoshiro256Random(42));
        foreach (var pref in nation.Prefectures)
        {
            var names = nation.InPrefecture(pref.Id).Select(s => s.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.All(names, n => Assert.EndsWith("高校", n));
        }
    }

    // --- 県別地名の導入が統計回帰帯を乱さないこと（不変条件#5の自動満足） ---

    [Fact]
    public void PrefectureVocab_DoesNotDisturbStrengthStreams()
    {
        // 県別地名を大量投入しても Generate は3ドロー固定 → 強さ/名声/校風/采配/地区の生成列は不変。
        // 校名（Name）だけが変わり、それ以外の全フィールドが school 単位で一致することを固定する（帯は再校正不要）。
        var placesById = new Dictionary<int, IReadOnlyList<string>>();
        for (var id = 0; id < 47; id++)
        {
            placesById[id] = new[] { "浜辺" + id, "山手" + id, "川沿" + id, "台原" + id, "湖畔" + id };
        }
        var enriched = Vocab with { PlacesByPrefecture = placesById };

        var baseline = NationGenerator.Generate(Vocab, Coeff, new Xoshiro256Random(2026));
        var withPlaces = NationGenerator.Generate(enriched, Coeff, new Xoshiro256Random(2026));

        Assert.Equal(baseline.Schools.Count, withPlaces.Schools.Count);
        var namesChanged = 0;
        for (var i = 0; i < baseline.Schools.Count; i++)
        {
            var a = baseline.Schools[i];
            var b = withPlaces.Schools[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.PrefectureId, b.PrefectureId);
            Assert.Equal(a.Establishment, b.Establishment);   // 私立率分岐（NextDouble）も不変
            Assert.Equal(a.Tradition, b.Tradition);
            Assert.Equal(a.Strength, b.Strength);
            Assert.Equal(a.Fame, b.Fame);
            Assert.Equal(a.Style, b.Style);
            Assert.Equal(a.TacticalSense, b.TacticalSense);
            Assert.Equal(a.DistrictId, b.DistrictId);
            if (a.Name != b.Name) namesChanged++;
        }
        // 公立校（多数）の校名は県別地名に差し替わっているはず（何も変わらないと配線ミス）。
        Assert.True(namesChanged > baseline.Schools.Count / 2, $"校名が変わった数 {namesChanged}");
    }

    [Fact]
    public void PrefectureVocab_UsesPrefectureSpecificPlaces()
    {
        // 指定県の公立校名に、その県の地名だけが現れる（他県の地名は混ざらない）。
        const int prefId = 5;
        var placesById = new Dictionary<int, IReadOnlyList<string>>
        {
            [prefId] = new[] { "羊蹄", "利尻", "礼文" },
        };
        var vocab = new SchoolNameVocab
        {
            PlacePrefixes = new[] { "共有甲", "共有乙" },
            PublicSuffixes = new[] { "北", "南" },
            PrivateStems = new[] { "私立X" },
            PrivateSuffixes = new[] { "学園" },
            PrivateRatio = 0.0, // 全て公立にして地名選択を検証
            PlacesByPrefecture = placesById,
        };
        var rng = new Xoshiro256Random(11);
        var hitPref = false;
        for (var i = 0; i < 60; i++)
        {
            var (name, est) = SchoolNameGenerator.Generate(vocab, rng, prefId);
            Assert.Equal(Establishment.Public, est);
            Assert.DoesNotContain("共有", name); // 県別リストがあるので共有へ落ちない
            if (name.StartsWith("羊蹄") || name.StartsWith("利尻") || name.StartsWith("礼文")) hitPref = true;
        }
        Assert.True(hitPref, "県別地名が使われていない");
    }

    [Fact]
    public void Generate_FallsBackToSharedPlaces_WhenPrefectureMissing()
    {
        // 県別リストが無い県・prefId 未指定は共有 place_prefixes を使う（後方互換）。
        var vocab = new SchoolNameVocab
        {
            PlacePrefixes = new[] { "共有甲" },
            PublicSuffixes = new[] { "西" },
            PrivateRatio = 0.0,
            PlacesByPrefecture = new Dictionary<int, IReadOnlyList<string>>
            {
                [3] = new[] { "県別地名" },
            },
        };
        var rng = new Xoshiro256Random(7);
        // prefId=99（未登録）→ 共有へフォールバック。
        var (name1, _) = SchoolNameGenerator.Generate(vocab, rng, 99);
        Assert.StartsWith("共有甲", name1);
        // prefId 未指定（既定 -1）→ 共有へフォールバック。
        var (name2, _) = SchoolNameGenerator.Generate(vocab, rng);
        Assert.StartsWith("共有甲", name2);
    }

    [Fact]
    public void Parser_ReadsSharedAndPrefectureLists()
    {
        const string yaml = @"
# コメント行
public_suffixes: [東, 西, 中央]
private_stems: [聖凛, 明星]
private_suffixes: [学院, 学園]
private_ratio: 0.30
place_prefixes: [青葉, 桜]

places_by_prefecture:
  hokkaido: [札幌, 函館,
             旭川]
  kanagawa: [横浜, 川崎, 湘南]
  99: [数値キー地名]
";
        var v = SchoolNameVocabParser.Parse(yaml);
        Assert.Equal(new[] { "東", "西", "中央" }, v.PublicSuffixes);
        Assert.Equal(new[] { "聖凛", "明星" }, v.PrivateStems);
        Assert.Equal(0.30, v.PrivateRatio, 6);
        Assert.Equal(new[] { "青葉", "桜" }, v.PlacePrefixes);
        // ローマ字キー → Id 解決（hokkaido=0, kanagawa=13）。複数行フロー・リストも連結される。
        Assert.Equal(new[] { "札幌", "函館", "旭川" }, v.PlacesByPrefecture[0]);
        Assert.Equal(new[] { "横浜", "川崎", "湘南" }, v.PlacesByPrefecture[13]);
        // 数値キーも直接解釈。
        Assert.Equal(new[] { "数値キー地名" }, v.PlacesByPrefecture[99]);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 選手フルネーム生成（苗字ランダム×名前ランダム・重み付き, 決定論）。
/// </summary>
public sealed class PlayerNameTests
{
    [Fact]
    public void Generate_ProducesFamilyGivenWithFullWidthSpace()
    {
        var name = PlayerNameGenerator.Generate(new PlayerNameVocab(), new Xoshiro256Random(1));
        Assert.Contains("　", name);                 // 全角スペース区切り
        var parts = name.Split('　');
        Assert.Equal(2, parts.Length);
        Assert.All(parts, p => Assert.False(string.IsNullOrWhiteSpace(p)));
    }

    [Fact]
    public void Generate_IsDeterministicForSameSeed()
    {
        var a = PlayerNameGenerator.Generate(new PlayerNameVocab(), new Xoshiro256Random(123));
        var b = PlayerNameGenerator.Generate(new PlayerNameVocab(), new Xoshiro256Random(123));
        Assert.Equal(a, b);
    }

    [Fact]
    public void PickWeighted_FavorsHeavierEntriesOverManyDraws()
    {
        // 重み10:1 の2択を1万回引くと、重い方が明確に多い（決定論なので固定シード）。
        var vocab = new PlayerNameVocab
        {
            FamilyNames = new List<WeightedName>
            {
                new() { Value = "重", Weight = 10.0 },
                new() { Value = "軽", Weight = 1.0 },
            },
            GivenNames = new List<WeightedName> { new() { Value = "太", Weight = 1.0 } },
        };
        var rng = new Xoshiro256Random(42);
        var heavy = 0;
        for (var i = 0; i < 10000; i++)
        {
            var name = PlayerNameGenerator.Generate(vocab, rng);
            if (name.StartsWith("重")) heavy++;
        }
        // 期待比率 ~10/11≈0.909。広めの帯で判定。
        Assert.InRange(heavy, 8500, 9500);
    }

    [Fact]
    public void ProspectGenerator_AssignsRealNames_NotPlaceholder()
    {
        var players = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(42));
        Assert.NotEmpty(players);
        Assert.All(players, p =>
        {
            Assert.DoesNotContain("年目入部", p.Name);   // 旧プレースホルダを廃止
            Assert.Contains("　", p.Name);               // フルネーム
        });
    }

    [Fact]
    public void Intake_IsDeterministic_SameSeedSameNames()
    {
        var a = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(7)).Select(p => p.Name).ToList();
        var b = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(7)).Select(p => p.Name).ToList();
        Assert.Equal(a, b);
    }

    /// <summary>同一チーム内で下の名前が重複しない（苗字の重複はOK）。issue #11 ①の回帰。</summary>
    [Fact]
    public void StrengthTeamFactory_HasNoDuplicateGivenNamesWithinTeam()
    {
        for (var schoolId = 0; schoolId < 300; schoolId++)
        {
            var team = StrengthTeamFactory.Create(55, "テスト高校",
                StrengthTeamFactory.SeedFor(schoolId, 1));
            var given = team.BattingOrder.Concat(team.Bullpen).Concat(team.Bench)
                .Select(p => GivenOf(p.Name)).ToList();
            Assert.True(given.Count >= 20, $"ベンチ入り {given.Count} 人（20人未満）");
            var dup = given.GroupBy(g => g).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dup.Count == 0, $"校Id {schoolId} で下の名前が重複: {string.Join(", ", dup)}");
        }
    }

    /// <summary>新入生の下の名前も同学年内・既存部員との間で重複しない。issue #11 ①の回帰。</summary>
    [Fact]
    public void Intake_HasNoDuplicateGivenNames_WithinIntakeAndAgainstExisting()
    {
        for (var seed = 1UL; seed <= 200UL; seed++)
        {
            var existing = new[] { "佐藤　大翔", "鈴木　蓮", "高橋　湊" };
            var players = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(seed),
                existingNames: existing);
            var given = players.Select(p => GivenOf(p.Name)).ToList();
            Assert.Equal(given.Count, given.Distinct().Count());
            Assert.DoesNotContain("大翔", given);
            Assert.DoesNotContain("蓮", given);
            Assert.DoesNotContain("湊", given);
        }
    }

    /// <summary>重複回避リロールは Fork ストリーム内で完結し、主RNGの消費列を変えない（不変条件#2）。</summary>
    [Fact]
    public void GivenNameDedupe_DoesNotDisturbAbilityRolls()
    {
        // 同シードの Intake は、重複回避の有無にかかわらず能力ロール（＝主RNG消費列）が一致する。
        var withDedupe = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(7),
            existingNames: new[] { "佐藤　大翔", "鈴木　蓮", "高橋　湊", "田中　樹", "伊藤　陸" });
        var plain = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(7));
        Assert.Equal(plain.Count, withDedupe.Count);
        Assert.Equal(plain.Select(p => p.AverageLevel()).ToList(),
            withDedupe.Select(p => p.AverageLevel()).ToList());
    }

    /// <summary>重みの平坦化（0.5乗圧縮）で最人気名の出現率が現実的な水準（約1%）に収まる。issue #11 ③。</summary>
    [Fact]
    public void GivenNameWeights_AreFlattened_TopNameIsAboutOnePercent()
    {
        var vocab = new PlayerNameVocab();
        Assert.True(vocab.GivenNames.Count >= 300, $"名前プールが {vocab.GivenNames.Count} 種（300未満）");

        var rng = new Xoshiro256Random(2026);
        var counts = new Dictionary<string, int>();
        const int draws = 40000;
        for (var i = 0; i < draws; i++)
        {
            var g = GivenOf(PlayerNameGenerator.Generate(vocab, rng));
            counts[g] = counts.TryGetValue(g, out var c) ? c + 1 : 1;
        }
        var topShare = counts.Values.Max() / (double)draws;
        // 圧縮前は上位が2.2〜2.9%だった（issue #11 実測）。圧縮後は現実の男子名分布（1位≒1%）並み。
        Assert.InRange(topShare, 0.005, 0.015);
    }

    /// <summary>data/player-names.yaml とベイク値 PlayerNameData が同内容（語彙の単一ソース維持）。</summary>
    [Fact]
    public void Yaml_AndBakedDefaults_AreInSync()
    {
        var yaml = PlayerNamesLoader.LoadFromFile(
            Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("player-names.yaml"));
        var baked = new PlayerNameVocab();

        Assert.Equal(baked.GivenWeightExponent, yaml.GivenWeightExponent, 6);
        AssertSameVocab(baked.FamilyNames, yaml.FamilyNames, "family_names");
        AssertSameVocab(baked.GivenNames, yaml.GivenNames, "given_names");

        // 名前プール内で値が一意（同じ名前を二重登録しない）。
        Assert.Equal(baked.GivenNames.Count, baked.GivenNames.Select(n => n.Value).Distinct().Count());
        Assert.Equal(baked.FamilyNames.Count, baked.FamilyNames.Select(n => n.Value).Distinct().Count());
    }

    private static void AssertSameVocab(IReadOnlyList<WeightedName> baked, IReadOnlyList<WeightedName> yaml, string key)
    {
        Assert.True(baked.Count == yaml.Count,
            $"{key}: ベイク {baked.Count} 件 / YAML {yaml.Count} 件で件数が違う");
        for (var i = 0; i < baked.Count; i++)
        {
            Assert.True(baked[i].Value == yaml[i].Value,
                $"{key}[{i}]: ベイク {baked[i].Value} / YAML {yaml[i].Value}");
            Assert.True(Math.Abs(baked[i].Weight - yaml[i].Weight) < 1e-9,
                $"{key}[{i}] ({baked[i].Value}): ベイク {baked[i].Weight} / YAML {yaml[i].Weight}");
        }
    }

    private static string GivenOf(string fullName)
    {
        var i = fullName.LastIndexOf('　');
        return i >= 0 ? fullName.Substring(i + 1) : fullName;
    }

    [Fact]
    public void PlayerNamesLoader_ParsesWeightedVocab()
    {
        const string yaml = @"
family_names:
  - { value: 佐藤, weight: 2.0 }
  - { value: 鈴木, weight: 1.0 }
given_names:
  - { value: 大翔, weight: 1.0 }
";
        var vocab = PlayerNamesLoader.Parse(yaml);
        Assert.Equal(2, vocab.FamilyNames.Count);
        Assert.Equal("佐藤", vocab.FamilyNames[0].Value);
        Assert.Equal(2.0, vocab.FamilyNames[0].Weight);
        Assert.Single(vocab.GivenNames);
    }

    [Fact]
    public void PlayerNamesLoader_FallsBackToDefaults_WhenKeyMissing()
    {
        // given_names を省略 → エンジン既定にフォールバック。
        var vocab = PlayerNamesLoader.Parse("family_names:\n  - { value: 佐藤, weight: 1.0 }\n");
        Assert.Single(vocab.FamilyNames);
        Assert.Equal(new PlayerNameVocab().GivenNames.Count, vocab.GivenNames.Count);
    }
}

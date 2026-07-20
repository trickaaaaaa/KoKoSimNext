using System.Collections.Generic;
using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Core;
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

using System;
using System.IO;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 実在校の私立名の除外（issue #1320）。除外に当たっても乱数の消費は変わらない（強さ・名声・校風の生成列と
/// 当たらなかった学校の名前を乱さない）こと、除外した名前は出ないこと、リポジトリの除外リストが語彙と噛み合うことを固定する。
/// </summary>
public class SchoolNameExclusionTests
{
    private static SchoolNameVocab PrivateOnly(params string[] excluded) => new()
    {
        PrivateStems = new[] { "明徳", "聖凛", "北都" },
        PrivateSuffixes = new[] { "義塾", "学院", "学園" },
        PrivateRatio = 1.0,
        ExcludedPrivateNames = excluded,
    };

    [Fact]
    public void ExcludedNames_NeverAppear()
    {
        var v = PrivateOnly("明徳義塾", "明徳学院", "聖凛学園");
        var rng = new Xoshiro256Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var (name, est) = SchoolNameGenerator.Generate(v, rng);
            Assert.Equal(Establishment.Private, est);
            Assert.DoesNotContain(name.Replace("高校", ""), v.ExcludedPrivateNames);
        }
    }

    [Fact]
    public void Exclusion_DoesNotChangeTheRandomDraws()
    {
        // 同じシードで、除外あり／なしの両方を回し、毎回の乱数の状態が一致すること＝引いた数が同じ。
        var with = new Xoshiro256Random(11);
        var without = new Xoshiro256Random(11);
        var vWith = PrivateOnly("明徳義塾", "明徳学院", "明徳学園");   // 語幹1つ丸ごと除外（語幹もずれる経路）
        var vWithout = PrivateOnly();
        for (var i = 0; i < 500; i++)
        {
            var (a, _) = SchoolNameGenerator.Generate(vWith, with);
            var (b, _) = SchoolNameGenerator.Generate(vWithout, without);
            Assert.Equal(without.CaptureState(), with.CaptureState());
            if (!vWith.ExcludedPrivateNames.Contains(b.Replace("高校", ""))) Assert.Equal(b, a);   // 当たらなければ同じ名前
        }
    }

    [Fact]
    public void ExcludedName_ShiftsToTheNextSuffix()
    {
        // 決定論的なずらし方（接尾を隣へ）を固定する。乱数で引き直すと生成列が乱れる。
        var v = PrivateOnly("明徳義塾");
        var rng = new Xoshiro256Random(1);
        for (var i = 0; i < 500; i++)
        {
            var probe = Xoshiro256Random.FromState(rng.CaptureState());
            probe.NextDouble();
            var si = probe.NextInt(0, 3);
            var xi = probe.NextInt(0, 3);
            var (name, _) = SchoolNameGenerator.Generate(v, rng);
            if (si == 0 && xi == 0) Assert.Equal("明徳学院高校", name);
        }
    }

    [Fact]
    public void Parser_ReadsTheExclusionList()
    {
        var v = SchoolNameVocabParser.Parse("private_stems: [明徳]\nprivate_suffixes: [義塾, 学院]\nexcluded_private_names: [明徳義塾]\n");
        Assert.Contains("明徳義塾", v.ExcludedPrivateNames);
    }

    [Fact]
    public void RepositoryExclusionList_MatchesTheVocabulary()
    {
        // 綴り違いの除外は黙って効かない。リストの全部が「語幹＋接尾」で作れる名前であること、
        // どの語幹も接尾が1つ以上残ること（全部除外だと語幹ごとずれて分布が偏る）を固定する。
        var v = SchoolNameVocabParser.Parse(File.ReadAllText(FindDataFile("school-names.yaml")));
        Assert.NotEmpty(v.ExcludedPrivateNames);
        var possible = v.PrivateStems.SelectMany(s => v.PrivateSuffixes.Select(x => s + x)).ToHashSet();
        foreach (var name in v.ExcludedPrivateNames)
            Assert.True(possible.Contains(name), $"除外リストの「{name}」は語幹＋接尾で作れない（綴り違い？）");
        foreach (var stem in v.PrivateStems)
            Assert.True(v.PrivateSuffixes.Any(x => !v.ExcludedPrivateNames.Contains(stem + x)), $"語幹「{stem}」の接尾が全部除外されている");
    }

    private static string FindDataFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"data/{fileName} がリポジトリ内に見つかりません。");
    }
}

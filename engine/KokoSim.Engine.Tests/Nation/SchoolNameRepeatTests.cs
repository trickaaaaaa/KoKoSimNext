using System;
using System.IO;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 公立の校名「地名＋特徴語」で字が重ならない（「南陽南陽」「湘南南」「仙台台」・issue #1276）。
/// 重なったら乱数を使わずに接尾を隣へずらす＝引いた数は変わらない。
/// </summary>
public class SchoolNameRepeatTests
{
    private static SchoolNameVocab PublicOnly(string[] places, string[] suffixes) => new()
    {
        PlacePrefixes = places,
        PublicSuffixes = suffixes,
        PrivateRatio = 0.0,
    };

    [Theory]
    [InlineData("南陽", "南陽", true)]
    [InlineData("中央", "中央", true)]
    [InlineData("湘南", "南", true)]
    [InlineData("洛北", "北", true)]
    [InlineData("仙台", "台", true)]
    [InlineData("北上", "北", false)]
    [InlineData("南陽", "南", false)]
    [InlineData("横浜", "東", false)]
    public void RepeatsCharacters_FlagsOnlyOverlapAtTheJoin(string place, string suffix, bool expected)
        => Assert.Equal(expected, SchoolNameGenerator.RepeatsCharacters(place, suffix));

    [Fact]
    public void RepositoryVocabulary_NeverMakesARepeatedName()
    {
        var v = SchoolNameVocabParser.Parse(File.ReadAllText(FindDataFile("school-names.yaml")));
        var rng = new Xoshiro256Random(3);
        var prefIds = new[] { -1 }.Concat(v.PlacesByPrefecture.Keys).ToList();
        foreach (var prefId in prefIds)
        {
            for (var i = 0; i < 3000; i++)
            {
                var (name, est) = SchoolNameGenerator.Generate(v, rng, prefId);
                if (est != Establishment.Public) continue;
                var body = name[..^2];
                var places = prefId >= 0 && v.PlacesByPrefecture.TryGetValue(prefId, out var l) && l.Count > 0 ? l : v.PlacePrefixes;
                var split = v.PublicSuffixes.SelectMany(s => places.Select(p => (p, s))).Where(t => t.p + t.s == body).ToList();
                Assert.NotEmpty(split);
                Assert.Contains(split, t => !SchoolNameGenerator.RepeatsCharacters(t.p, t.s));
            }
        }
    }

    [Fact]
    public void Shifting_DoesNotChangeTheRandomDraws()
    {
        var v = PublicOnly(new[] { "南陽", "横浜" }, new[] { "南陽", "東", "西" });
        var rng = new Xoshiro256Random(5);
        var plain = new Xoshiro256Random(5);
        for (var i = 0; i < 500; i++)
        {
            var (name, _) = SchoolNameGenerator.Generate(v, rng);
            plain.NextDouble(); plain.NextInt(0, 2); plain.NextInt(0, 3);
            Assert.Equal(plain.CaptureState(), rng.CaptureState());
            Assert.NotEqual("南陽南陽高校", name);
        }
    }

    [Fact]
    public void Shifting_MovesToTheNextSuffix_AndKeepsTheDrawWhenThereIsNoOverlap()
    {
        var v = PublicOnly(new[] { "南陽" }, new[] { "南陽", "東" });
        var names = Enumerable.Range(0, 200).Select(i => SchoolNameGenerator.Generate(v, new Xoshiro256Random((ulong)i)).Name).Distinct().ToList();
        Assert.Equal(new[] { "南陽東高校" }, names);
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

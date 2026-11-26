using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KokoSim.Config;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// issue #1275: <see cref="BalanceTargets"/> 等の C# 既定帯が data/balance-targets.yaml から古びていた
/// （TripleRate 0.009 vs 0.015 など）。YAML から鍵を消すと、古い既定で黙って通ってしまう。
/// 読み込んだ帯と C# 既定（<c>new XxxTargets()</c>）が全プロパティで一致することを固定する（片方だけ変えない）。
/// 必須（required）の帯は既定を持たないので対象外。
/// </summary>
public sealed class BalanceTargetsDefaultsConsistencyTests
{
    [Fact]
    public void CSharpDefaultBands_MatchRepositoryBalanceTargetsYaml()
    {
        var yaml = File.ReadAllText(FindDataFile("balance-targets.yaml"));
        var parsed = new (string Section, object Value)[]
        {
            ("atbat_10k", BalanceTargetsLoader.Parse(yaml)),
            ("games_10k", BalanceTargetsLoader.ParseGame(yaml)),
            ("games_10k_tactics", BalanceTargetsLoader.ParseGameTactics(yaml)),
            ("tiers_low", BalanceTargetsLoader.ParseLowTier(yaml)),
            ("tiers_low_mercy", BalanceTargetsLoader.ParseLowTierMercy(yaml)),
            ("matchup_grid", BalanceTargetsLoader.ParseMatchupGrid(yaml)),
            ("nation_dominance", BalanceTargetsLoader.ParseNationDominance(yaml)),
        };

        var mismatches = new List<string>();
        foreach (var (section, value) in parsed)
        {
            var defaults = Activator.CreateInstance(value.GetType())!;
            foreach (var prop in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(Band) && prop.PropertyType != typeof(double)) continue;
                var expected = prop.GetValue(defaults);
                if (expected is null) continue;
                var actual = prop.GetValue(value);
                if (!Equals(expected, actual))
                    mismatches.Add($"{section}.{prop.Name}: C#既定={expected} / yaml={actual}");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"C#既定の帯と data/balance-targets.yaml が {mismatches.Count} 件乖離（YAML へ揃える）:\n" + string.Join("\n", mismatches));
    }

    private static string FindDataFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "data", name);
            if (File.Exists(path)) return path;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(name);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KokoSim.Config;
using Xunit;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// data/coefficients.yaml の鍵が<b>すべてローダの DTO に束縛されている</b>こと。
/// ローダは <c>IgnoreUnmatchedProperties()</c> なので、YAML に鍵を足して DTO への1行を忘れると
/// **黙って C# 既定が使われ**、YAML を編集しても sim/Heavy に効かない（2026-08-18 実害: 4本の新規鍵が未束縛だった。
/// <c>CoefficientsDefaultsConsistencyTests</c> は「YAML=既定」なので素通りする）。
/// 各セクションの直下の鍵（scalar/mapping）を DTO の公開プロパティ名（snake_case）と突き合わせる。
/// </summary>
public sealed class CoefficientsYamlKeysAreBoundTests
{
    [Fact]
    public void EveryYamlKey_HasADtoProperty()
    {
        var path = FindDataFile("coefficients.yaml");
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path)) yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;

        var loaderType = typeof(CoefficientsLoader);
        var dtoType = loaderType.GetNestedType("CoefficientsDto", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CoefficientsDto が見つからない");
        var unbound = new List<string>();
        Walk(root, dtoType, "", unbound);

        Assert.True(unbound.Count == 0,
            "data/coefficients.yaml に DTO 未束縛の鍵（YAML を編集しても効かない）:\n" + string.Join("\n", unbound));
    }

    /// <summary>
    /// data/balance-targets.yaml も同じ罠を踏む（実害 2026-08-18: <c>doubles_per_homerun</c> は規約変換だと
    /// <c>doubles_per_home_run</c> になり束縛されず、帯を YAML でいくら動かしても C# 既定 1.30–2.60 が使われていた）。
    /// <c>version</c> だけは束縛の要らないメタ鍵として除外する。
    /// </summary>
    [Fact]
    public void EveryBalanceTargetsKey_HasADtoProperty()
    {
        var path = FindDataFile("balance-targets.yaml");
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path)) yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;

        var dtoType = typeof(BalanceTargetsLoader).GetNestedType("RootDto", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RootDto が見つからない");
        var unbound = new List<string>();
        Walk(root, dtoType, "", unbound);
        unbound.Remove("version");

        Assert.True(unbound.Count == 0,
            "data/balance-targets.yaml に DTO 未束縛の鍵（YAML を編集しても効かない）:\n" + string.Join("\n", unbound));
    }

    private static void Walk(YamlMappingNode node, Type dtoType, string path, List<string> unbound)
    {
        // [YamlMember(Alias=…)] が付いていればその名前、無ければ規約変換（snake_case）で束縛される。
        var props = dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(
                p => p.GetCustomAttribute<YamlDotNet.Serialization.YamlMemberAttribute>()?.Alias
                     ?? UnderscoredNamingConvention.Instance.Apply(p.Name),
                p => p, StringComparer.Ordinal);
        foreach (var (k, v) in node.Children)
        {
            var key = ((YamlScalarNode)k).Value ?? "";
            if (!props.TryGetValue(key, out var prop))
            {
                unbound.Add($"{path}{key}");
                continue;
            }
            // 入れ子の mapping は DTO の型が「プロパティを持つクラス」のときだけ潜る（Dictionary/List は素通し）。
            if (v is YamlMappingNode child)
            {
                var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                if (t.IsClass && t != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
                    Walk(child, t, $"{path}{key}.", unbound);
            }
        }
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
        throw new FileNotFoundException(fileName);
    }
}

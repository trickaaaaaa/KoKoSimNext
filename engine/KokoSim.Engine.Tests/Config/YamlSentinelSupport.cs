using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// issue #1212: 係数 YAML の数値の葉を1つずつ別の「目印の値」へ書き換えた YAML を作り、
/// 読み込み後の Model から数値を拾い集める道具（YAML の値が Model・試合文脈まで届くかの検査用）。
/// </summary>
internal static class YamlSentinelSupport
{
    public sealed record SentinelYaml(string Text, IReadOnlyDictionary<string, double> Markers);

    public static SentinelYaml Build(string yamlPath)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(yamlPath)) yaml.Load(reader);
        var leaves = new List<(string Path, YamlScalarNode Node, bool IsInt)>();
        CollectLeaves(yaml.Documents[0].RootNode, "", leaves);

        var markers = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < leaves.Count; i++)
        {
            var (path, node, isInt) = leaves[i];
            var marker = isInt ? 1_000_000 + i : 500_000.0 + i * 0.01;
            markers[path] = marker;
            node.Value = isInt
                ? ((long)marker).ToString(CultureInfo.InvariantCulture)
                : marker.ToString("R", CultureInfo.InvariantCulture);
        }

        using var sw = new StringWriter();
        yaml.Save(sw, assignAnchors: false);
        return new SentinelYaml(sw.ToString(), markers);
    }

    public static long Key(double v) => (long)Math.Round(v * 100.0);

    /// <summary>Model 内の数値（double/float/整数）を、辞書・リスト・public フィールドも含めて出現順に集める。</summary>
    public static List<double> NumbersOf(object? model)
    {
        var list = new List<double>();
        Walk(model, list, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
        return list;
    }

    private static void CollectLeaves(YamlNode node, string path, List<(string, YamlScalarNode, bool)> leaves)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (k, v) in map.Children)
                {
                    var key = ((YamlScalarNode)k).Value ?? "";
                    if (path.Length == 0 && key == "version") continue;
                    CollectLeaves(v, path.Length == 0 ? key : $"{path}.{key}", leaves);
                }
                break;
            case YamlSequenceNode seq:
                for (var i = 0; i < seq.Children.Count; i++) CollectLeaves(seq.Children[i], $"{path}[{i}]", leaves);
                break;
            case YamlScalarNode s when s.Style == ScalarStyle.Plain && s.Value is not null:
                if (long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    leaves.Add((path, s, true));
                else if (s.Value.Any(char.IsDigit)
                         && double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    leaves.Add((path, s, false));
                break;
        }
    }

    private static void Walk(object? o, List<double> acc, HashSet<object> visited, int depth)
    {
        if (o is null || depth > 12) return;
        var t = o.GetType();
        switch (o)
        {
            case double d: acc.Add(d); return;
            case float f: acc.Add(f); return;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                acc.Add(Convert.ToDouble(o, CultureInfo.InvariantCulture)); return;
        }
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)) return;
        if (!t.IsValueType && !visited.Add(o)) return;

        if (o is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
            {
                Walk(e.Key, acc, visited, depth + 1);
                Walk(e.Value, acc, visited, depth + 1);
            }
            return;
        }
        if (o is IEnumerable en)
        {
            foreach (var x in en) Walk(x, acc, visited, depth + 1);
            return;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            Walk(t.GetProperty("Key")!.GetValue(o), acc, visited, depth + 1);
            Walk(t.GetProperty("Value")!.GetValue(o), acc, visited, depth + 1);
            return;
        }
        if (!(t.Namespace?.StartsWith("KokoSim", StringComparison.Ordinal) ?? false)) return;

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            object? v;
            try { v = p.GetValue(o); } catch { continue; }
            Walk(v, acc, visited, depth + 1);
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Walk(f.GetValue(o), acc, visited, depth + 1);
    }

    public static string FindDataFile(string fileName)
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

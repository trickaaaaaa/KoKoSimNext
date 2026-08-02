using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using KokoSim.Config;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// issue #402: 実ゲーム（Unity 実プレイ・Balance CLI の係数指定なし）は data/coefficients.yaml を読まず
/// C#既定（各 <c>new XxxCoefficients()</c>）で走る。C#既定と YAML がズレると
/// 「校正済みの帯を測っているのに実ゲームは校正前」になる（不変条件#4/#5 に抵触）。
///
/// このテストは <see cref="CoefficientsLoader.LoadFromFile"/> で data/coefficients.yaml から読んだ各係数オブジェクトを、
/// 対応する C#既定（引数なし <c>new</c>、または静的 <c>Default</c>）と全プロパティ再帰比較し、
/// 両者が完全一致することを固定する。片方だけ動かすと即赤になる（＝再発防止の本体）。
///
/// リフレクションで <see cref="CoefficientsBundle"/> のメンバを総なめするので、将来 bundle に係数クラスや
/// プロパティを足しても手書き不要で自動的に対象へ入る。
/// </summary>
public sealed class CoefficientsDefaultsConsistencyTests
{
    // 意図的な乖離（アラインしない）:
    //   Version … 係数ではなくファイル版数。
    //   Weather … data/coefficients.yaml:313 の記述通り、実プレイ側（GameContext.Weather=null）では無効で
    //             sim/Heavy 回帰でだけ効かせる「意図された sim 限定の乖離」。実ゲームへの反映（C#既定を
    //             非nullにする）は決定論 baseline 再生成を伴う後続作業として明示的に切り出されている。
    private static readonly HashSet<string> ExcludedMembers = new(StringComparer.Ordinal) { "Version", "Weather" };

    [Fact]
    public void CSharpDefaults_MatchRepositoryCoefficientsYaml()
    {
        var bundle = CoefficientsLoader.LoadFromFile(FindDataFile("coefficients.yaml"));
        var mismatches = new List<string>();

        foreach (var prop in typeof(CoefficientsBundle).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (ExcludedMembers.Contains(prop.Name)) continue;
            var loaded = prop.GetValue(bundle);
            if (loaded is null) continue;
            if (!IsCoefficientObject(loaded.GetType())) continue; // Version 等のスカラは対象外
            var expected = DefaultFor(loaded.GetType());
            CompareRecursive(prop.Name, expected, loaded, mismatches);
        }

        Assert.True(mismatches.Count == 0,
            $"C#既定 と data/coefficients.yaml が {mismatches.Count} 件乖離（issue #402: 実ゲームは C#既定で走るため YAML へ揃える）:\n"
            + string.Join("\n", mismatches));
    }

    // --- 再帰比較 ---

    private static void CompareRecursive(string path, object? expected, object? actual, List<string> mismatches)
    {
        if (expected is null && actual is null) return;
        if (expected is null || actual is null)
        {
            mismatches.Add($"{path}: 既定={Fmt(expected)} / yaml={Fmt(actual)}");
            return;
        }

        var t = actual.GetType();

        if (t == typeof(double) || t == typeof(float))
        {
            var e = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
            var a = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
            if (Math.Abs(e - a) > 1e-9) mismatches.Add($"{path}: 既定={Fmt(expected)} / yaml={Fmt(actual)}");
            return;
        }

        if (IsScalar(t))
        {
            if (!Equals(expected, actual)) mismatches.Add($"{path}: 既定={Fmt(expected)} / yaml={Fmt(actual)}");
            return;
        }

        if (actual is IEnumerable ae && expected is IEnumerable ee)
        {
            var al = ae.Cast<object?>().ToList();
            var el = ee.Cast<object?>().ToList();
            if (al.Count != el.Count)
            {
                mismatches.Add($"{path}.Count: 既定={el.Count} / yaml={al.Count}");
                return;
            }
            for (var i = 0; i < al.Count; i++) CompareRecursive($"{path}[{i}]", el[i], al[i], mismatches);
            return;
        }

        // 係数オブジェクト（KokoSim 名前空間のレコード/クラス）: 書き込み可能プロパティを再帰比較する。
        // 計算プロパティ（get-only）は CanWrite==false なので自動的に対象外になる。
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            CompareRecursive($"{path}.{p.Name}", p.GetValue(expected), p.GetValue(actual), mismatches);
        }
    }

    private static bool IsScalar(Type t)
        => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);

    private static bool IsCoefficientObject(Type t)
        => !IsScalar(t)
           && !typeof(IEnumerable).IsAssignableFrom(t)
           && (t.Namespace?.StartsWith("KokoSim", StringComparison.Ordinal) ?? false);

    /// <summary>C#既定インスタンス。静的 <c>Default</c>（例: FacilityCatalog）があればそれ、無ければ引数なし new。</summary>
    private static object DefaultFor(Type t)
    {
        var defProp = t.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
        if (defProp is not null && defProp.PropertyType == t) return defProp.GetValue(null)!;
        var defField = t.GetField("Default", BindingFlags.Public | BindingFlags.Static);
        if (defField is not null && defField.FieldType == t) return defField.GetValue(null)!;
        return Activator.CreateInstance(t)!;
    }

    private static string Fmt(object? v) => v switch
    {
        null => "null",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "null",
    };

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

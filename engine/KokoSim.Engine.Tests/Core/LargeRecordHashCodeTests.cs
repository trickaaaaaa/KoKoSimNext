using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using KokoSim.Engine.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Core;

/// <summary>
/// ブラウザ版（Unity Web＝IL2CPP→Emscripten）でエンジンがコンパイルできることの見張り。
///
/// <para>C# の record は全プロパティを1つの式でつないだ GetHashCode を自動で作る。IL2CPP はそれを
/// 入れ子の関数呼び出しの C++ に変換し、Emscripten の clang は括弧の入れ子が256段を超えると
/// 「bracket nesting level exceeded maximum of 256」で止まる（2026-11-21 の Web お試しビルドで FieldingCoefficients が該当）。
/// 1プロパティで3段ほど深くなるので上限はおよそ85個。<b>プロパティの多い record は GetHashCode を自分で書く</b>。
/// Windows 版（Mono）や dotnet test では起きないので、ここで型を数えて先に止める。</para>
/// </summary>
public class LargeRecordHashCodeTests
{
    /// <summary>この数を超えたら自動生成の GetHashCode に任せない（上限約85に余裕を持たせる）。</summary>
    private const int MaxPropertiesForSynthesizedHash = 60;

    [Fact]
    public void LargeRecords_DeclareTheirOwnGetHashCode()
    {
        var offenders = typeof(FieldingCoefficients).Assembly.GetTypes()
            .Where(IsRecord)
            .Select(t => (Type: t, Count: t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Length))
            .Where(x => x.Count > MaxPropertiesForSynthesizedHash && HasSynthesizedHash(x.Type))
            .Select(x => $"{x.Type.FullName}（プロパティ {x.Count} 個）")
            .ToList();

        Assert.True(offenders.Count == 0,
            "プロパティの多い record が自動生成の GetHashCode のまま＝ブラウザ版がコンパイルできない。"
            + "`public override int GetHashCode() => GetType().GetHashCode();` を足す:\n" + string.Join("\n", offenders));
    }

    private static bool IsRecord(Type t)
        => t.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.Public) != null;

    private static bool HasSynthesizedHash(Type t)
    {
        var m = t.GetMethod(nameof(GetHashCode), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        return m != null && m.GetCustomAttribute<CompilerGeneratedAttribute>() != null;
    }
}

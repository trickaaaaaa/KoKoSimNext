using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Season;

/// <summary>重み付き語彙エントリ（値＋出現重み）。</summary>
public sealed record WeightedName
{
    public string Value { get; init; } = "";
    public double Weight { get; init; } = 1.0;
}

/// <summary>
/// 選手フルネーム生成の語彙（苗字プール／名前プール）。既定値は data/player-names.yaml のミラー
/// （engine/KokoSim.Engine/Season/PlayerNameData）。YAMLで差し替え可能（KokoSim.Config）。
/// </summary>
public sealed record PlayerNameVocab
{
    /// <summary>苗字プール（全国占率で重み付け）。</summary>
    public IReadOnlyList<WeightedName> FamilyNames { get; init; } = PlayerNameData.FamilyNames;

    /// <summary>名前プール（男子名, 順位で重み付け）。</summary>
    public IReadOnlyList<WeightedName> GivenNames { get; init; } = PlayerNameData.GivenNames;
}

/// <summary>
/// 苗字ランダム × 名前ランダムでフルネームを組み立てる（設計書06/氏名生成）。
/// 抽選は重み付きルーレット（EventScheduler と同型）。乱数は注入（決定論・不変条件#2）。
/// </summary>
public static class PlayerNameGenerator
{
    /// <summary>「苗字　名前」（全角スペース区切り）を返す。</summary>
    public static string Generate(PlayerNameVocab v, IRandomSource rng)
    {
        var family = PickWeighted(v.FamilyNames, rng);
        var given = PickWeighted(v.GivenNames, rng);
        if (family.Length == 0) return given;
        if (given.Length == 0) return family;
        return family + "　" + given;
    }

    /// <summary>重み合計×NextDouble→累積減算で1件選ぶ（空/重み0は先頭または空文字にフォールバック）。</summary>
    private static string PickWeighted(IReadOnlyList<WeightedName> list, IRandomSource rng)
    {
        if (list == null || list.Count == 0) return "";

        var total = 0.0;
        foreach (var e in list) total += e.Weight > 0 ? e.Weight : 0;
        if (total <= 0) return list[rng.NextInt(0, list.Count)].Value; // 全重み0なら均等

        var roll = rng.NextDouble() * total;
        foreach (var e in list)
        {
            if (e.Weight <= 0) continue;
            roll -= e.Weight;
            if (roll <= 0) return e.Value;
        }
        return list[list.Count - 1].Value; // 数値誤差の保険
    }
}

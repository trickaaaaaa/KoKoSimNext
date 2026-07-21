using System;
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
/// 圧縮後の実効重みを内部キャッシュするため record ではなく class（値等価・with は使わない）。
/// </summary>
public sealed class PlayerNameVocab
{
    /// <summary>苗字プール（全国占率で重み付け）。</summary>
    public IReadOnlyList<WeightedName> FamilyNames { get; init; } = PlayerNameData.FamilyNames;

    /// <summary>名前プール（男子名, 順位で重み付け）。</summary>
    public IReadOnlyList<WeightedName> GivenNames { get; init; } = PlayerNameData.GivenNames;

    /// <summary>
    /// 名前重みの圧縮指数（0&lt;e≦1）。生の順位重みを e 乗して上位偏重を平坦化する。
    /// 既定 0.5 で「1位≒出現率1%」＝現実の男子名分布に近づく（人気名は出やすいが画面内で埋め尽くさない）。
    /// </summary>
    public double GivenWeightExponent { get; init; } = PlayerNameData.GivenWeightExponent;

    // 圧縮後の実効重み（語彙は不変なので初回抽選時に一度だけ算出してキャッシュする）。
    private double[]? _givenEffective;

    internal double[] GivenEffectiveWeights => _givenEffective ??= Compress(GivenNames, GivenWeightExponent);

    private static double[] Compress(IReadOnlyList<WeightedName> list, double exponent)
    {
        var result = new double[list?.Count ?? 0];
        if (list == null) return result;
        for (var i = 0; i < list.Count; i++)
        {
            var w = list[i].Weight;
            result[i] = w > 0 ? Math.Pow(w, exponent) : 0;
        }
        return result;
    }
}

/// <summary>
/// 苗字ランダム × 名前ランダムでフルネームを組み立てる（設計書06/氏名生成）。
/// 抽選は重み付きルーレット（EventScheduler と同型）。乱数は注入（決定論・不変条件#2）。
/// </summary>
public static class PlayerNameGenerator
{
    /// <summary>同一チーム内で下の名前が被ったときのリロール上限（超えたら未使用語彙を走査して確定）。</summary>
    private const int MaxGivenRerolls = 24;

    /// <summary>「苗字　名前」（全角スペース区切り）を返す。</summary>
    public static string Generate(PlayerNameVocab v, IRandomSource rng)
        => Generate(v, rng, null);

    /// <summary>
    /// 下の名前が <paramref name="usedGivenNames"/> と被らないフルネームを返し、採用した名前を同集合へ登録する。
    /// 苗字の重複は許す（現実にも同姓の部員はいる）。リロールは渡された rng ストリーム内で完結するため、
    /// 呼び出し側が Fork したストリームを渡すかぎり主RNGの消費列は1ビットも変わらない（決定論・不変条件#2）。
    /// </summary>
    public static string Generate(PlayerNameVocab v, IRandomSource rng, ISet<string>? usedGivenNames)
    {
        var family = PickWeighted(v.FamilyNames, null, rng);
        var given = PickGiven(v, rng, usedGivenNames);
        usedGivenNames?.Add(given);
        if (family.Length == 0) return given;
        if (given.Length == 0) return family;
        return family + "　" + given;
    }

    /// <summary>未使用の下の名前を引く。リロール上限に達したら語彙を順に走査して未使用の1件を採る。</summary>
    private static string PickGiven(PlayerNameVocab v, IRandomSource rng, ISet<string>? used)
    {
        var weights = v.GivenEffectiveWeights;
        var given = PickWeighted(v.GivenNames, weights, rng);
        if (used == null || !used.Contains(given)) return given;

        for (var i = 0; i < MaxGivenRerolls; i++)
        {
            given = PickWeighted(v.GivenNames, weights, rng);
            if (!used.Contains(given)) return given;
        }
        // 語彙が枯れかけている場合の確定手段（乱数由来のオフセットから走査＝偏りを作らない）。
        var list = v.GivenNames;
        if (list == null || list.Count == 0) return given;
        var start = rng.NextInt(0, list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var candidate = list[(start + i) % list.Count].Value;
            if (!used.Contains(candidate)) return candidate;
        }
        return given;   // 語彙を使い切った（人数＞語彙数）。重複を許容する。
    }

    /// <summary>
    /// 重み合計×NextDouble→累積減算で1件選ぶ（空/重み0は先頭または空文字にフォールバック）。
    /// <paramref name="weights"/> を渡すとその実効重みを使う（null なら entry.Weight をそのまま使う）。
    /// </summary>
    private static string PickWeighted(IReadOnlyList<WeightedName> list, double[]? weights, IRandomSource rng)
    {
        if (list == null || list.Count == 0) return "";

        double WeightAt(int i)
        {
            var w = weights != null ? weights[i] : list[i].Weight;
            return w > 0 ? w : 0;
        }

        var total = 0.0;
        for (var i = 0; i < list.Count; i++) total += WeightAt(i);
        if (total <= 0) return list[rng.NextInt(0, list.Count)].Value; // 全重み0なら均等

        var roll = rng.NextDouble() * total;
        for (var i = 0; i < list.Count; i++)
        {
            var w = WeightAt(i);
            if (w <= 0) continue;
            roll -= w;
            if (roll <= 0) return list[i].Value;
        }
        return list[list.Count - 1].Value; // 数値誤差の保険
    }
}

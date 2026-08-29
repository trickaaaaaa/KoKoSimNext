using System;
using System.Collections.Generic;
using System.Globalization;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 記録室・チーム内ランキングの部門（issue #600）。表示名・並べ方・規定の要否はすべて
/// <see cref="RecordRoom"/> が持つ＝画面ごとに「打率は降順」「防御率は規定投球回」を書き直さない。
/// </summary>
public enum RecordCategory
{
    // 打撃
    Average, Hits, HomeRuns, Rbi, StolenBases, Obp, Ops,
    // 投手（勝敗投手は概念ごと撤去＝高校野球では主役でない・監督判断2026-08-27）
    Era, StrikeOuts, InningsPitched, Whip,
}

/// <summary>
/// 順位付けの候補1件（issue #600）。通算スコープは「選手1人」、シーズンスコープは「選手1人の1学年ぶん」。
/// どちらも同じ形にすることで <see cref="RecordRoom.Top"/> を1本で使い回す。
/// 成績は元の帳簿を書き換えないよう<b>複製して</b>持つ（表示専用の集計）。
/// </summary>
public sealed class RecordCandidate
{
    /// <summary>成績の帰属キー（DevelopingPlayer.Id）。氏名の解決は Unity 側（卒業生も引ける）。</summary>
    public int PlayerId { get; set; }

    /// <summary>年度（YearIndex）。0＝年を持たない（通算スコープ・issue #600 より前のアーカイブ）。</summary>
    public int Year { get; set; }

    /// <summary>当時の学年 1〜3。0＝通算スコープ。</summary>
    public int Grade { get; set; }

    public BattingStatLine Batting { get; } = new();
    public PitchingStatLine Pitching { get; } = new();
}

/// <summary>順位表の1件（値は並べ替え用の生値、Display は表示用の整形済み文字列）。</summary>
public readonly record struct RecordEntry(int PlayerId, int Year, int Grade, double Value, string Display);

/// <summary>
/// 校歴代の個人記録・チーム内ランキングを組み立てる純関数（issue #600・設計書06 §3.9）。
///
/// <para>集計を Unity 側に書かないための単一ソース。記録タブ（校歴代1位）とホームのチーム内ランキングは
/// この同じ関数を通る＝規定打席の定義が2箇所に散らない（<c>TeamStatsService</c> の予告どおり）。</para>
///
/// <para>乱数を引かず物理層も通らない＝<b>表示専用</b>。ここを変えても統計帯は動かない。</para>
/// </summary>
public static class RecordRoom
{
    private static readonly RecordsCoefficients Defaults = new();

    /// <summary>記録タブの打撃部門（設計書06 §3.2「打率・本塁打・打点・盗塁＋OPS」）。</summary>
    public static readonly IReadOnlyList<RecordCategory> BattingCategories = new[]
    {
        RecordCategory.Average, RecordCategory.HomeRuns, RecordCategory.Rbi,
        RecordCategory.StolenBases, RecordCategory.Ops,
    };

    /// <summary>記録タブの投手部門（高校野球なので勝敗を主役にしない＝投球回で酷使を見せる）。</summary>
    public static readonly IReadOnlyList<RecordCategory> PitchingCategories = new[]
    {
        RecordCategory.Era, RecordCategory.StrikeOuts, RecordCategory.InningsPitched, RecordCategory.Whip,
    };

    // ===== 部門の性質 =====

    /// <summary>部門の表示名（用語集に従う。画面ごとに文字列を作らない）。</summary>
    public static string LabelOf(RecordCategory c) => c switch
    {
        RecordCategory.Average => "打率",
        RecordCategory.Hits => "安打",
        RecordCategory.HomeRuns => "本塁打",
        RecordCategory.Rbi => "打点",
        RecordCategory.StolenBases => "盗塁",
        RecordCategory.Obp => "出塁率",
        RecordCategory.Ops => "OPS",
        RecordCategory.Era => "防御率",
        RecordCategory.StrikeOuts => "奪三振",
        RecordCategory.InningsPitched => "投球回",
        RecordCategory.Whip => "WHIP",
        _ => "",
    };

    /// <summary>投手部門か（打撃部門と区別して区画見出しを出すため）。</summary>
    public static bool IsPitching(RecordCategory c) => c switch
    {
        RecordCategory.Era or RecordCategory.StrikeOuts
            or RecordCategory.InningsPitched or RecordCategory.Whip => true,
        _ => false,
    };

    /// <summary>小さいほど良い部門（防御率・WHIP）。</summary>
    public static bool LowerIsBetter(RecordCategory c)
        => c == RecordCategory.Era || c == RecordCategory.Whip;

    /// <summary>並べ替えに使う生値（投球回はアウト数＝1/3回まで比較できる）。</summary>
    public static double ValueOf(RecordCategory c, RecordCandidate x) => c switch
    {
        RecordCategory.Average => x.Batting.Average,
        RecordCategory.Hits => x.Batting.Hits,
        RecordCategory.HomeRuns => x.Batting.HomeRuns,
        RecordCategory.Rbi => x.Batting.Rbi,
        RecordCategory.StolenBases => x.Batting.StolenBases,
        RecordCategory.Obp => x.Batting.Obp,
        RecordCategory.Ops => x.Batting.Ops,
        RecordCategory.Era => x.Pitching.Era,
        RecordCategory.StrikeOuts => x.Pitching.StrikeOuts,
        RecordCategory.InningsPitched => x.Pitching.Outs,
        RecordCategory.Whip => x.Pitching.Whip,
        _ => 0.0,
    };

    /// <summary>表示用の整形済み文字列（率は野球慣行の3桁表記、投球回は「84 1/3」）。</summary>
    public static string DisplayOf(RecordCategory c, RecordCandidate x) => c switch
    {
        RecordCategory.Average => Rate3(x.Batting.Average),
        RecordCategory.Obp => Rate3(x.Batting.Obp),
        RecordCategory.Ops => Rate3(x.Batting.Ops),
        RecordCategory.Era => x.Pitching.Era.ToString("0.00", CultureInfo.InvariantCulture),
        RecordCategory.Whip => x.Pitching.Whip.ToString("0.00", CultureInfo.InvariantCulture),
        RecordCategory.InningsPitched => x.Pitching.InningsText,
        _ => ((int)ValueOf(c, x)).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>野球慣行の3桁表記（0.312 → .312、1.104 はそのまま）。</summary>
    public static string Rate3(double v)
    {
        var s = v.ToString("0.000", CultureInfo.InvariantCulture);
        return s.StartsWith("0.", StringComparison.Ordinal) ? s.Substring(1) : s;
    }

    // ===== 規定打席・規定投球回 =====

    /// <summary>
    /// そのスコープの規定打席・規定投球回[アウト]（<see cref="RecordsCoefficients"/>）。
    /// 基準のチーム試合数は<b>選手の最大出場試合数</b>で代用する（チーム試合数のカウンタが無いため）。
    /// </summary>
    public static (int PlateAppearances, int Outs) QualificationOf(
        IEnumerable<RecordCandidate> scope, RecordsCoefficients? coefficients = null)
    {
        var games = 0;
        if (scope != null)
            foreach (var x in scope)
            {
                if (x.Batting.Games > games) games = x.Batting.Games;
                if (x.Pitching.Games > games) games = x.Pitching.Games;
            }
        return ThresholdFor(games, coefficients ?? Defaults);
    }

    /// <summary>帳簿1冊ぶんの規定（ホームのチーム内ランキングが引く入口）。</summary>
    public static (int PlateAppearances, int Outs) QualificationOf(
        StatBook book, RecordsCoefficients? coefficients = null)
        => QualificationOf(CareerCandidates(book), coefficients);

    private static (int PlateAppearances, int Outs) ThresholdFor(int teamGames, RecordsCoefficients c)
        => (Math.Max(1, (int)Math.Ceiling(teamGames * c.QualifiedPlateAppearancesPerGame)),
            Math.Max(1, (int)Math.Ceiling(teamGames * c.QualifiedOutsPerGame)));

    /// <summary>その部門の順位表に載る資格があるか（率系は規定、数系は1以上）。</summary>
    public static bool Qualifies(RecordCategory c, RecordCandidate x, int paNeeded, int outsNeeded) => c switch
    {
        RecordCategory.Average or RecordCategory.Obp or RecordCategory.Ops
            => x.Batting.PlateAppearances >= paNeeded && x.Batting.AtBats > 0,
        RecordCategory.Era or RecordCategory.Whip => x.Pitching.Outs >= outsNeeded,
        _ => ValueOf(c, x) > 0,
    };

    // ===== 順位表 =====

    /// <summary>
    /// 部門の上位 <paramref name="count"/> 件。該当者が居なければ短い配列を返す（呼び出し側が空枠を埋める）。
    ///
    /// <para>規定は<b>年度ごと</b>に出す＝シーズン記録で「大会を1回しか勝ち上がれなかった年」と
    /// 「決勝まで行った年」を同じ閾値で測らない。通算スコープは全件が Year=0 の1群になる。</para>
    ///
    /// <para>同値は選手ID→年度の昇順で確定させる（決定論・並びがぶれない）。</para>
    /// </summary>
    public static List<RecordEntry> Top(
        IReadOnlyList<RecordCandidate> candidates, RecordCategory category, int count,
        RecordsCoefficients? coefficients = null)
    {
        var result = new List<RecordEntry>();
        if (candidates == null || candidates.Count == 0 || count <= 0) return result;

        var c = coefficients ?? Defaults;

        // 年度ごとのチーム試合数（＝最大出場試合数）を1パスで集める。
        var gamesByYear = new Dictionary<int, int>();
        foreach (var x in candidates)
        {
            var g = Math.Max(x.Batting.Games, x.Pitching.Games);
            if (!gamesByYear.TryGetValue(x.Year, out var cur) || g > cur) gamesByYear[x.Year] = g;
        }
        var thresholds = new Dictionary<int, (int Pa, int Outs)>();
        foreach (var kv in gamesByYear) thresholds[kv.Key] = ThresholdFor(kv.Value, c);

        var pool = new List<RecordCandidate>();
        foreach (var x in candidates)
        {
            var t = thresholds[x.Year];
            if (Qualifies(category, x, t.Pa, t.Outs)) pool.Add(x);
        }

        var lower = LowerIsBetter(category);
        pool.Sort((a, b) =>
        {
            var cmp = ValueOf(category, a).CompareTo(ValueOf(category, b));
            if (!lower) cmp = -cmp;
            if (cmp != 0) return cmp;
            var byId = a.PlayerId.CompareTo(b.PlayerId);
            return byId != 0 ? byId : a.Year.CompareTo(b.Year);
        });

        var take = Math.Min(count, pool.Count);
        for (var i = 0; i < take; i++)
        {
            var x = pool[i];
            result.Add(new RecordEntry(x.PlayerId, x.Year, x.Grade, ValueOf(category, x), DisplayOf(category, x)));
        }
        return result;
    }

    // ===== 候補の作り方（スコープ2種） =====

    /// <summary>通算スコープ（帳簿1冊＝選手1人1件・年を持たない）。</summary>
    public static List<RecordCandidate> CareerCandidates(StatBook book)
    {
        var list = new List<RecordCandidate>();
        if (book == null) return list;
        foreach (var kv in book.Players)
        {
            var cand = new RecordCandidate { PlayerId = kv.Key };
            cand.Batting.Merge(kv.Value.Batting);
            cand.Pitching.Merge(kv.Value.Pitching);
            list.Add(cand);
        }
        // 辞書の列挙順は不定なので並べ替える（同値のときの順位が揺れない＝決定論）。
        list.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
        return list;
    }

    /// <summary>
    /// シーズンスコープ（大会別アーカイブを「選手×学年」＝1シーズンへ畳む）。
    /// 1つの学年の夏と秋は同じ年度なので合算して1件にできる（<see cref="ArchivedPlayerStats.Year"/>）。
    /// 専用の保存箱は要らない＝アーカイブがそのまま源。
    /// </summary>
    public static List<RecordCandidate> SeasonCandidates(TournamentArchive archive)
    {
        var list = new List<RecordCandidate>();
        if (archive == null) return list;

        var map = new Dictionary<(int PlayerId, int Grade), RecordCandidate>();
        foreach (var key in archive.Keys)
        {
            var slot = archive.Slot(key);
            if (slot is null) continue;
            foreach (var kv in slot)
            {
                var mapKey = (kv.Key, key.Grade);
                if (!map.TryGetValue(mapKey, out var cand))
                {
                    cand = new RecordCandidate { PlayerId = kv.Key, Grade = key.Grade };
                    map[mapKey] = cand;
                    list.Add(cand);
                }
                cand.Batting.Merge(kv.Value.Batting);
                cand.Pitching.Merge(kv.Value.Pitching);
                if (kv.Value.Year > cand.Year) cand.Year = kv.Value.Year;   // 0=不明は上書きしない
            }
        }
        list.Sort((a, b) =>
        {
            var byId = a.PlayerId.CompareTo(b.PlayerId);
            return byId != 0 ? byId : a.Grade.CompareTo(b.Grade);
        });
        return list;
    }
}

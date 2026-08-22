using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// data/pref-formats/regional-tournaments.yaml を読む純パーサ（IO・YamlDotNet依存なし＝不変条件#3）。
/// Unity は KokoSim.Config（YamlDotNet）を持たないため、StreamingAssets に置いた同じ YAML をこれで読み、
/// Balance/CLI/テストが使う <see cref="KokoSim.Config"/> の完全ロードと**同じ値**（<see cref="RegionalFormatSet"/>）
/// を返す（<see cref="PrefFormatDistrictParser"/> と同型。正典は data/ のまま＝不変条件#4）。
///
/// <para>見る構造は3階層のみ: <c>regions:</c> 配下の各 <c>- region: xxx</c> ブロック、その中の
/// スカラーキー（<c>single_prefecture</c>/<c>host_prefecture_bonus</c>/<c>champion_to_jingu</c>）と
/// インラインマップ（<c>{ k: v, ... }</c>）、および <c>biennial_rotation:</c> 直下の
/// <c>odd_year:</c>/<c>even_year:</c>（インラインマップ）。<c>notes:</c> は読み捨てる。</para>
/// </summary>
public static class RegionalFormatParser
{
    public static RegionalFormatSet Parse(string yaml)
    {
        var regions = new List<RegionalFormat>();

        string? region = null;
        var singlePrefecture = false;
        var hostPrefectureBonus = false;
        var championToJingu = true;
        var fixedBerths = new Dictionary<string, int>();
        var berthsPerPref = new Dictionary<string, int>();
        Dictionary<string, int>? oddYear = null;
        Dictionary<string, int>? evenYear = null;
        var hostByYear = new Dictionary<int, string>();

        var regionIndent = -1;   // "- region:" の後続キー（berths_per_pref 等）の字下げ段
        var inBiennial = false;
        var biennialIndent = -1; // odd_year/even_year の字下げ段
        var inNotes = false;

        void CloseRegion()
        {
            if (region == null) return;
            regions.Add(new RegionalFormat
            {
                Region = region,
                SinglePrefecture = singlePrefecture,
                HostPrefectureBonus = hostPrefectureBonus,
                ChampionToJingu = championToJingu,
                FixedBerths = fixedBerths,
                BerthsPerPref = berthsPerPref,
                OddYearBerths = oddYear,
                EvenYearBerths = evenYear,
                HostByYear = hostByYear,
            });
            region = null;
            singlePrefecture = false;
            hostPrefectureBonus = false;
            championToJingu = true;
            fixedBerths = new Dictionary<string, int>();
            berthsPerPref = new Dictionary<string, int>();
            oddYear = null;
            evenYear = null;
            hostByYear = new Dictionary<int, string>();
            inBiennial = false;
            inNotes = false;
        }

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw);
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var indent = LeadingSpaces(line);

            if (indent == 0) { continue; } // "regions:" 自身。何もしない。

            // 新しいリージョンブロックの開始。
            if (trimmed.StartsWith("- region:"))
            {
                CloseRegion();
                regionIndent = indent + 2; // "- " のぶんだけ内側がリージョン自身のキー段
                var (_, regionName) = Split(trimmed.Substring(1).Trim());
                region = regionName;
                continue;
            }
            if (region == null) continue; // リージョン開始前のゴミ行は無視

            if (indent < regionIndent) { CloseRegion(); continue; }

            if (inBiennial)
            {
                if (indent >= biennialIndent)
                {
                    var (bk, bv) = Split(trimmed);
                    if (bk == "odd_year") oddYear = ParseInlineIntMap(bv);
                    else if (bk == "even_year") evenYear = ParseInlineIntMap(bv);
                    continue;
                }
                inBiennial = false; // 段が戻った＝biennial_rotation ブロック終了
            }
            if (inNotes)
            {
                if (indent > regionIndent) continue; // notes: の箇条書き行
                inNotes = false;
            }
            if (indent != regionIndent) continue; // 想定外の深い段（あれば読み捨てる）

            var (k, v) = Split(trimmed);
            if (k == null) continue;
            switch (k)
            {
                case "single_prefecture": singlePrefecture = v == "true"; break;
                case "host_prefecture_bonus": hostPrefectureBonus = v == "true"; break;
                case "champion_to_jingu": championToJingu = v == "true"; break;
                case "fixed": fixedBerths = ParseInlineIntMap(v); break;
                case "berths_per_pref": berthsPerPref = ParseInlineIntMap(v); break;
                case "host_by_year": hostByYear = ParseInlineYearMap(v); break;
                case "biennial_rotation": inBiennial = true; biennialIndent = regionIndent + 2; break;
                case "notes": inNotes = true; break;
            }
        }
        CloseRegion();

        return new RegionalFormatSet(regions);
    }

    /// <summary>インラインマップ <c>{ aomori: 3, iwate: 3 }</c> → 県名(ローマ字)→枠数。</summary>
    private static Dictionary<string, int> ParseInlineIntMap(string value)
    {
        var map = new Dictionary<string, int>();
        foreach (var (k, v) in SplitInlineMap(value))
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) map[k] = n;
        return map;
    }

    /// <summary>インラインマップ <c>{ 2025: iwate, 2026: gunma }</c> → 年→県名(ローマ字)。</summary>
    private static Dictionary<int, string> ParseInlineYearMap(string value)
    {
        var map = new Dictionary<int, string>();
        foreach (var (k, v) in SplitInlineMap(value))
            if (int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)) map[year] = v;
        return map;
    }

    private static IEnumerable<(string Key, string Value)> SplitInlineMap(string value)
    {
        var inner = value.Trim();
        if (inner.StartsWith("{")) inner = inner.Substring(1);
        if (inner.EndsWith("}")) inner = inner.Substring(0, inner.Length - 1);
        if (inner.Trim().Length == 0) yield break;

        foreach (var entry in inner.Split(','))
        {
            var (k, v) = Split(entry.Trim());
            if (k != null) yield return (k, v);
        }
    }

    private static string StripComment(string line)
    {
        var i = line.IndexOf('#');
        return i < 0 ? line : line.Substring(0, i);
    }

    private static (string? Key, string Value) Split(string trimmed)
    {
        var colon = trimmed.IndexOf(':');
        if (colon < 0) return (null, "");
        return (trimmed.Substring(0, colon).Trim(), trimmed.Substring(colon + 1).Trim());
    }

    private static int LeadingSpaces(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }
}

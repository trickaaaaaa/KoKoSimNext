using System.Collections.Generic;
using System.Globalization;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// data/pref-formats/&lt;県&gt;.yaml から**県内地区の数だけ**を抜く純パーサ（IO・依存なし＝不変条件#3）。
/// 新設校スタート（issue #585）の地区選択が要るのは「その県に県内地区があるか／いくつか」の2点だけで、
/// 大会フォーマット全体（<see cref="PrefFormat"/>）を Unity 側で組む必要はない。
///
/// 正典は <c>data/pref-formats/</c> のまま（不変条件#4）。Balance/CLI は Config 層の完全ロードを、
/// Unity は StreamingAssets へ複製した同じ YAML をこのパーサで読む＝**同じファイルの同じ値**を見る。
/// <see cref="DistrictPlan.Build"/> が完全ロードから作る「県Id→地区数」と同じ意味の値を返す。
///
/// 見るのは次の3つだけ:
///   - 先頭の <c>pref: kanagawa</c>（ローマ字県名）
///   - <c>district_assignment: geographic</c>（地理固定割でなければ地区は無い＝0）
///   - <c>stages:</c> の中で <c>type: group_split</c> かつ <c>grouping: geographic</c> のステージの <c>groups: N</c>
/// ステージの入れ子（<c>child:</c> 配下の type）を拾わないよう、**ステージ自身の字下げ段だけ**を見る。
/// </summary>
public static class PrefFormatDistrictParser
{
    /// <summary>1県ぶんの YAML から (ローマ字県名, 地区数) を返す。地区が無い県は Districts=0。</summary>
    public static (string Pref, int Districts) Parse(string yaml)
    {
        string pref = "";
        var geographic = false;

        // stages: 配下のステージ1件ぶんの状態。ステージの区切り（"- " 始まり）で確定・リセットする。
        var inStages = false;
        var stageIndent = -1;
        string? stageType = null, stageGrouping = null;
        int? stageGroups = null;
        var found = 0;

        void CloseStage()
        {
            if (found == 0 && stageType == "group_split" && stageGrouping == "geographic" && stageGroups is { } g)
                found = g;
            stageType = null; stageGrouping = null; stageGroups = null;
        }

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw);
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var indent = LeadingSpaces(line);

            if (indent == 0)
            {
                CloseStage();
                inStages = false;
                stageIndent = -1;
                var (k0, v0) = Split(trimmed);
                if (k0 == null) continue;
                if (k0 == "pref") pref = v0;
                else if (k0 == "district_assignment") geographic = v0 == "geographic";
                else if (k0 == "stages") inStages = true;
                continue;
            }

            if (!inStages) continue;

            // ステージの開始（"- name: 地区予選" / "- type: knockout"）。
            if (trimmed.StartsWith("-"))
            {
                CloseStage();
                stageIndent = indent + 2;   // "- " のぶんだけ内側がステージ自身のキー段
                trimmed = trimmed.Substring(1).Trim();
                if (trimmed.Length == 0) continue;
            }
            else if (indent != stageIndent)
            {
                continue;   // child: 配下など、ステージ自身より深い（or 浅い）段は見ない
            }

            var (k, v) = Split(trimmed);
            if (k == null) continue;
            switch (k)
            {
                case "type": stageType = v; break;
                case "grouping": stageGrouping = v; break;
                case "groups":
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) stageGroups = n;
                    break;
            }
        }
        CloseStage();

        return (pref, geographic ? found : 0);
    }

    /// <summary>複数県ぶんの YAML テキストから「県Id → 地区数」を作る（地区0の県は載せない）。</summary>
    public static IReadOnlyDictionary<int, int> BuildPlan(IEnumerable<string> yamlTexts)
    {
        var map = new Dictionary<int, int>();
        foreach (var text in yamlTexts)
        {
            var (pref, districts) = Parse(text);
            if (districts <= 0) continue;
            var id = PrefectureNames.IdOf(pref);
            if (id >= 0) map[id] = districts;
        }
        return map;
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

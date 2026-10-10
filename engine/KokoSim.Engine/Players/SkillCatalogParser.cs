using System.Collections.Generic;

namespace KokoSim.Engine.Players;

/// <summary>
/// data/skills.yaml を <see cref="SkillCatalogEntry"/> の列へ変換する純パーサ（IO・依存なし＝不変条件#3）。
/// <see cref="Nation.SchoolNameVocabParser"/>/<see cref="Nation.TeamColorParser"/> と同じ流儀で、
/// Config層（<c>KokoSim.Config.SkillsCatalogLoader</c>）と Unity が同一コードで解釈する
/// （issue skills-yaml-not-data-driven: 二重管理の解消）。
///
/// 対応する構文は本ファイルが想定する部分集合のみ:
///   - 全行コメント（先頭が <c>#</c>）と空行は無視（行末コメントは非対応＝skills.yamlは使わない）
///   - <c>skills:</c> 直下の <c>- id: X</c> で始まるブロック・リスト、以降のインデント行が同じ項目の
///     <c>key: value</c>（<c>category</c>/<c>hidden_eligible</c>/<c>name</c>/<c>description</c>）
/// 未知の id・category を持つ項目は黙って捨てる（呼び元がフォールバック/警告を選べる、他パーサと同流儀）。
/// </summary>
public static class SkillCatalogParser
{
    public static IReadOnlyList<SkillCatalogEntry> Parse(string yaml)
    {
        var list = new List<SkillCatalogEntry>();
        string? id = null, category = null, name = null, description = null;
        var hiddenEligible = false;
        var inSkills = false;

        void Flush()
        {
            if (id is { Length: > 0 }
                && System.Enum.TryParse<Skill>(id, out var skillId)
                && TryCategory(category, out var cat))
            {
                list.Add(new SkillCatalogEntry(skillId, cat, name ?? "", description ?? "", hiddenEligible));
            }
            id = category = name = description = null;
            hiddenEligible = false;
        }

        foreach (var raw in yaml.Split('\n'))
        {
            var trimmed = raw.TrimEnd('\r').TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            if (!inSkills)
            {
                if (trimmed.StartsWith("skills:")) inSkills = true;
                continue;
            }

            if (trimmed.StartsWith("- "))
            {
                Flush();
                trimmed = trimmed.Substring(2).TrimStart();
            }

            var colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            var key = trimmed.Substring(0, colon).Trim();
            var value = trimmed.Substring(colon + 1).Trim();

            switch (key)
            {
                case "id": id = value; break;
                case "category": category = value; break;
                case "hidden_eligible": hiddenEligible = value == "true"; break;
                case "name": name = value; break;
                case "description": description = value; break;
            }
        }
        Flush();
        return list;
    }

    private static bool TryCategory(string? raw, out SkillCategory category)
    {
        switch (raw)
        {
            case "batting": category = SkillCategory.Batting; return true;
            case "pitching": category = SkillCategory.Pitching; return true;
            case "fielding": category = SkillCategory.Fielding; return true;
            case "team": category = SkillCategory.Team; return true;
            case "constitution": category = SkillCategory.Constitution; return true;
            case "special": category = SkillCategory.Special; return true;
            default: category = default; return false;
        }
    }
}

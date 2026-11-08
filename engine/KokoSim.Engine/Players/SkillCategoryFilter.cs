using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Players;

/// <summary>
/// 見えている特殊能力を分類で絞る（issue #781）。試合 HUD の打者帯は打撃だけ・投手帯は投手だけを出す
/// （場面に効く能力を一目で拾える＝UI原則⑥）。分類は data/skills.yaml の <c>category:</c> が単一ソースで、
/// 呼び出し側はカタログから引いた分類を <paramref name="categoryOf"/> で渡す（能力IDごとに手書きしない）。
/// </summary>
public static class SkillCategoryFilter
{
    /// <summary>
    /// <paramref name="skills"/> の見えている能力のうち、分類が <paramref name="category"/> のものを定義順で返す。
    /// 隠し能力は出さない（設計書10 §6）。分類が引けない能力は含めない（どの帯のものか分からないため）。
    /// </summary>
    public static IReadOnlyList<Skill> VisibleIn(
        SkillSet? skills, Func<Skill, SkillCategory?> categoryOf, SkillCategory category)
    {
        var list = new List<Skill>();
        if (skills is null) return list;
        foreach (var s in skills.Visible)
            if (categoryOf(s) == category) list.Add(s);
        list.Sort();   // 集合の列挙順に頼らず定義順で並べる
        return list;
    }
}

using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation;

/// <summary>
/// AI校の年次進化（設計書05 §2.3）。全4000校をフルシムせずティア抽象で更新する。
/// 平均回帰（安定化）＋世代交代の揺らぎ（勢力変動）＋大会成績・名声の反映。
/// </summary>
public static class AiSchoolModel
{
    /// <summary>
    /// 1年分の進化を適用。winsBySchool は当年の全大会（地方＋甲子園）の勝利数, championId は甲子園王者。
    /// </summary>
    public static void Evolve(
        Nation nation,
        IReadOnlyDictionary<int, int> winsBySchool,
        int championId,
        NationCoefficients c,
        IRandomSource rng)
    {
        var mean = c.StrengthMean;

        // 名声→強さ寄与の基準は母集団の当年平均名声（固定50ではない）。
        // 母集団の名声は無勝校で毎年 fame_decay により50未満へ沈むため、固定50を基準にすると
        // 「有名でない＝強さ下押し」が全校に系統的にかかり平均強さが単調ドリフトする（Phase4の痩せ）。
        // 平均を基準にすればこの項は母集団でゼロサム化し、相対的な「有名＝強い」だけが残る（集約ドリフト消滅）。
        var meanFame = nation.Schools.Count > 0 ? nation.Schools.Average(s => s.Fame) : 50.0;

        foreach (var s in nation.Schools)
        {
            // 平均回帰（暴走防止）。
            var strength = s.Strength + (mean - s.Strength) * c.MeanReversion;

            // 世代交代の揺らぎ。
            strength += rng.NextGaussian(0, c.ChurnSd);

            // 大会成績と名声の反映。
            var wins = winsBySchool.TryGetValue(s.Id, out var w) ? w : 0;
            strength += wins * c.StrengthPerWin;
            strength += (s.Fame - meanFame) * c.FameToStrength;

            s.Strength = MathUtil.Clamp(strength, c.StrengthMin, c.StrengthMax);

            // 名声更新: 減衰 ＋ 成績。
            var fame = s.Fame * c.FameDecay + wins * c.FamePerWin;
            if (s.Id == championId) fame += c.FameChampion;
            s.Fame = MathUtil.Clamp(fame, 0, 100);
        }
    }
}

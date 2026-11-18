using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Season;

/// <summary>
/// 対左投手経験の実戦蓄積（設計書02 §2.3「左のプレミア」・issue #1072）。<see cref="MatchGrowthModel"/>（精神力/
/// 走塁判断/捕手リード）と同じ合流点で適用する別モデル。詳細試合の <see cref="GameResult"/> ボックススコア
/// （<see cref="Match.Game.BattingLine.PlateAppearancesVsLeft"/>、SourceId=DevelopingPlayer.Id）と自校ロスターを
/// 突き合わせ、左投手と対戦した打席ぶんを加重して <see cref="DevelopingPlayer.LeftExperiencePa"/> へ積む。
/// 乱数を一切使わない決定論処理（不変条件#2）で、公式戦/練習試合の重みだけが違う（Q38と同じ流儀）。
/// 上限クランプはしない（<see cref="ContactModel.LeftExperienceDeficit"/> 側で飽和させる）。
/// </summary>
public static class LeftExperienceGrowthModel
{
    /// <summary>
    /// 1試合ぶんの対左投手経験を適用する。<paramref name="managerIsAway"/>=true なら detail の Away 側が自校。
    /// <paramref name="isOfficial"/> で公式戦/練習試合の重み（<see cref="BattingCoefficients.LeftExperienceOfficialPaWeight"/>/
    /// <see cref="BattingCoefficients.LeftExperiencePracticePaWeight"/>）を選ぶ（<see cref="BatteryChemistry.AddGame"/> と同じ区分）。
    /// </summary>
    public static void Apply(
        GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster,
        bool isOfficial, BattingCoefficients c)
    {
        var weight = isOfficial ? c.LeftExperienceOfficialPaWeight : c.LeftExperiencePracticePaWeight;
        if (weight <= 0.0) return;

        var batting = managerIsAway ? detail.AwayBatting : detail.HomeBatting;
        var byId = new Dictionary<int, DevelopingPlayer>(roster.Count);
        foreach (var dp in roster) byId[dp.Id] = dp;

        foreach (var line in batting)
        {
            if (line.SourceId is not { } id || line.PlateAppearancesVsLeft <= 0) continue;
            if (!byId.TryGetValue(id, out var dp)) continue;
            dp.LeftExperiencePa += line.PlateAppearancesVsLeft * weight;
        }
    }
}

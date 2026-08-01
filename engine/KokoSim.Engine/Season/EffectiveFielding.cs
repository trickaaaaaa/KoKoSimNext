using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;

namespace KokoSim.Engine.Season;

/// <summary>
/// 守備位置適性→実効守備力（設計書01 §1.1）。Fielding（地力）×AptitudeFactor（適性補正,
/// 基準50で×1.0）の単一ソース。<see cref="RosterTeamBuilder.ToPlayer"/> と UI の判断画面
/// （実効ランク表示, issue #371）が同じ式を通るための共有ヘルパ。
/// 怪我・スランプ等の一時係数は含まない（実効守備力は健常時のロスター評価値）。
/// </summary>
public static class EffectiveFielding
{
    /// <summary>丸め・クランプ前の生値＝ Fielding × AptitudeFactor(適性)。</summary>
    public static double Raw(DevelopingPlayer player, FieldPosition position, FieldingCoefficients coeff)
        => player.Level(AbilityKind.Fielding) * coeff.AptitudeFactor(player.Aptitude(position));

    /// <summary>実効守備力（1〜100、四捨五入）。適性50（中庸）で地力の Fielding と一致する。</summary>
    public static int Rank(DevelopingPlayer player, FieldPosition position, FieldingCoefficients coeff)
        => (int)MathUtil.Clamp(System.Math.Round(Raw(player, position, coeff)), 1, 100);
}

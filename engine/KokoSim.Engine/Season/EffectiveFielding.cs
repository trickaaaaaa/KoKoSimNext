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
        => Raw(player.Level(AbilityKind.Fielding), player.Aptitude(position), coeff);

    /// <summary>
    /// 素の守備力と適性から直接引く形（issue #384）。AI校は <see cref="DevelopingPlayer"/> を持たず
    /// 投影済みの能力＋適性配列で割当を解くので、式を写さずここを共有する（不変条件#1: 変換式は一箇所）。
    /// </summary>
    public static double Raw(int fieldingLevel, int aptitude, FieldingCoefficients coeff)
        => fieldingLevel * coeff.AptitudeFactor(aptitude);

    /// <summary>実効守備力（1〜100、四捨五入）。適性50（中庸）で地力の Fielding と一致する。</summary>
    public static int Rank(DevelopingPlayer player, FieldPosition position, FieldingCoefficients coeff)
        => (int)MathUtil.Clamp(System.Math.Round(Raw(player, position, coeff)), 1, 100);
}

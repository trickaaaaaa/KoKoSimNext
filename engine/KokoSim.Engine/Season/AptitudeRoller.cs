using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Season;

/// <summary>
/// 守備位置適性（投手含む全9ポジ）の創発ロール（設計書01 §1.1）。地力(base)＋系統(投/捕/内/外)ごとの
/// 独立な向き不向き＋ポジ個体差の和。自校 <see cref="ProspectGenerator"/>（Issue #174）と
/// AI校 <see cref="Nation.Roster.AiRosterFactory"/>（Issue #177）が同じロールを共有する。
/// </summary>
public static class AptitudeRoller
{
    /// <summary>全9守備位置（enum順）。</summary>
    public static readonly FieldPosition[] AllPositions =
    {
        FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    private static bool IsInfield(FieldPosition p) =>
        p is FieldPosition.FirstBase or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop;

    /// <summary>
    /// 9守備位置の適性(1〜99)を (int)FieldPosition で添字付けた配列にロールする。rng は独立ストリーム
    /// （呼び出し側で Fork したもの）を渡すこと＝主RNGの消費列を変えない。
    /// </summary>
    public static int[] Roll(RosterCoefficients c, IRandomSource rng)
    {
        var baseLevel = rng.NextGaussian(c.AptitudeBaseMean, c.AptitudeBaseSd);
        // 系統ごとの向き不向き（独立）。捕手・投手はそれぞれ独立の専門系統。
        var affPitcher = rng.NextGaussian(0, c.AptitudeGroupAffinitySd);
        var affCatcher = rng.NextGaussian(0, c.AptitudeGroupAffinitySd);
        var affInfield = rng.NextGaussian(0, c.AptitudeGroupAffinitySd);
        var affOutfield = rng.NextGaussian(0, c.AptitudeGroupAffinitySd);

        var result = new int[AllPositions.Length];
        foreach (var pos in AllPositions)
        {
            var affinity = pos switch
            {
                FieldPosition.Pitcher => affPitcher,
                FieldPosition.Catcher => affCatcher,
                _ when IsInfield(pos) => affInfield,
                _ => affOutfield,
            };
            var v = (int)MathUtil.Clamp(
                Math.Round(baseLevel + affinity + rng.NextGaussian(0, c.AptitudePositionNoiseSd)), 1, 99);
            result[(int)pos] = v;
        }
        return result;
    }

    /// <summary>投手を除く8守備位置中の最良適性（投手適性との比較に使う, Issue #174/#177）。</summary>
    public static int BestField(int[] apt)
    {
        var best = int.MinValue;
        foreach (var pos in AllPositions)
            if (pos != FieldPosition.Pitcher) best = Math.Max(best, apt[(int)pos]);
        return best;
    }
}

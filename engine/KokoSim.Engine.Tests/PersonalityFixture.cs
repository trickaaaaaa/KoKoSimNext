using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Tests;

/// <summary>
/// テスト用: 性格タイプ名から4軸を組む（設計書23 §1）。タイプは軸の導出ラベルになったので、
/// 「熱血漢の選手」を作るときはそのマスの軸の値を置く。既存8タイプは種の表の代表値、
/// 新しい8タイプはマスの側の 75／25、Normal は全部50。
/// </summary>
internal static class PersonalityFixture
{
    private static readonly PersonalityCoefficients PC = new();

    /// <summary>②素直さ・③勤勉さ・④姿勢。</summary>
    public static (int Receptiveness, int Diligence, int Selflessness) Axes(Personality t)
    {
        if (t == Personality.Normal) return (50, 50, 50);
        var pp = PC.Profile(t);
        if (pp.SpawnWeight > 0) return (pp.Receptiveness, pp.Diligence, pp.Selflessness);
        int V(PersonalityAxis a) => Personalities.CellHigh(t, a) ? 75 : 25;
        return (V(PersonalityAxis.Receptiveness), V(PersonalityAxis.Diligence), V(PersonalityAxis.Selflessness));
    }

    /// <summary>そのマスの側にある統率傾向（高い側 70／低い側 30）。</summary>
    public static int Leadership(Personality t)
        => t == Personality.Normal ? 50 : Personalities.CellHigh(t, PersonalityAxis.Leadership) ? 70 : 30;

    /// <summary>試合用選手に性格の軸を置く（統率は触らない）。</summary>
    public static Player WithAxes(this Player p, Personality t)
    {
        var (r, d, s) = Axes(t);
        return p with { Receptiveness = r, Diligence = d, Selflessness = s };
    }

    /// <summary>試合用選手に性格の軸と、マスの側の統率を置く（ラベルが t になる）。</summary>
    public static Player WithPersonality(this Player p, Personality t)
        => p.WithAxes(t) with { Leadership = Leadership(t) };

    /// <summary>そのタイプの育成選手（統率もマスの側）。</summary>
    public static DevelopingPlayer Developing(Personality t, int? leadership = null, double selfGrowthJitter = 0.0)
    {
        var (r, d, s) = Axes(t);
        return new DevelopingPlayer
        {
            Leadership = leadership ?? Leadership(t),
            Receptiveness = r,
            Diligence = d,
            Selflessness = s,
            SelfGrowthJitter = selfGrowthJitter,
        };
    }
}

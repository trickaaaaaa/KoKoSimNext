using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 自校カードでも「背景カードと同量の本流消費」を保つためのダミー消費（集計モデル経路のみ）。
/// フルシムでは背景も Fork のため消費ゼロ＝自校カードも消費ゼロで整合する（何もしない）。
/// 消費順は <c>AggregateMatch.PlayDetailed</c> → 敗者スコア <c>NextInt(0,4)</c>（TournamentRunner.SynthesizeScore と同じ）。
/// </summary>
internal static class LegacyManagerCardRng
{
    public static void Consume(School a, School b, NationCoefficients coeff, IRandomSource rng, bool fullSimBackground)
    {
        if (fullSimBackground) return;
        AggregateMatch.PlayDetailed(a, b, coeff, rng);
        rng.NextInt(0, 4);
    }
}

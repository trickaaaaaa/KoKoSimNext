using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.AtBat;

/// <summary>
/// 打席解決パイプライン①〜⑥（設計書01 §2）のエントリ。1球単位の解決は <see cref="AtBatSession"/> が担い、
/// ここは打席確定まで一括 drain する薄いラッパ（設計書15 Phase A）。すべて注入された IRandomSource で決定論
/// （不変条件#2）。
/// </summary>
public static class AtBatResolver
{
    /// <summary>規定球数上限（ファウル粘り等で打席が終わらない場合の安全打ち切り）。</summary>
    internal const int MaxPitches = 40;

    /// <summary>Phase 1 互換 API。結果のみ返す。</summary>
    public static PlateAppearanceResult Resolve(
        BatterAttributes batter,
        PitcherAttributes pitcher,
        AtBatContext ctx,
        IRandomSource rng)
        => ResolveDetailed(batter, pitcher, ctx, rng).Result;

    /// <summary>
    /// 結果＋投球数を返す（試合エンジン用）。<see cref="AtBatSession"/> を打席確定まで一括 drain するだけの
    /// 薄いラッパ（設計書15 Phase A）。1球ずつ外部から <see cref="AtBatSession.ThrowNextPitch"/> を回した場合と
    /// 実装が完全に同一＝逐次進行（1球境界の采配窓）と一括解決が原理的に一致する。
    /// </summary>
    public static AtBatResult ResolveDetailed(
        BatterAttributes batter,
        PitcherAttributes pitcher,
        AtBatContext ctx,
        IRandomSource rng)
    {
        var session = AtBatSession.Begin(batter, pitcher, ctx);
        while (!session.IsComplete)
        {
            session.ThrowNextPitch(rng);
        }
        return session.Result;
    }
}

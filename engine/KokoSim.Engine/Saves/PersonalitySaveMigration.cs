using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 性格の4軸化（設計書23 §1.5・issue #932・セーブ v27）の読み込み側の移行。乱数は使わない＝ロードのたびに同じ結果。
///
/// <para>v26 までのセーブは性格を8タイプの列挙（<c>personality</c>）で持っていた。軸のキー（<c>receptiveness</c> 等）が
/// 無ければ、旧タイプのマスの<b>代表値</b>（<see cref="PersonalityCoefficients"/> の種の表）へ置き換える。
/// 統率傾向（<c>leadership</c>）は既に個人の値を持っているので触らない。</para>
///
/// <para>自校部員（<see cref="Season.DevelopingPlayer"/>）は旧 <c>personalityFactor</c>（自主成長の個人倍率）を持っていたので、
/// 勤勉さはその値から<b>逆算</b>する（自主成長の効きが移行で変わらないように）。ただし逆算した値がタイプのマスの反対側へ
/// またいだら、マスの側の端（50 か 49）で止める＝旧タイプの名前（②③④の側）を保つ。</para>
/// </summary>
internal static class PersonalitySaveMigration
{
    private static readonly PersonalityCoefficients Defaults = new();

    /// <summary>試合用 Player（AI校ロスター等）の②③④。</summary>
    public static (int Receptiveness, int Diligence, int Selflessness) Axes(JsonObject o)
    {
        if (o.Has("receptiveness"))
            return (o.Int("receptiveness"), o.Int("diligence"), o.Int("selflessness"));
        var legacy = Legacy(o);
        var pp = Defaults.Profile(legacy);
        return (pp.Receptiveness, pp.Diligence, pp.Selflessness);
    }

    /// <summary>自校部員の②③④（③は旧 personalityFactor から逆算）。</summary>
    public static (int Receptiveness, int Diligence, int Selflessness) DevelopingAxes(JsonObject o)
    {
        if (o.Has("receptiveness"))
            return (o.Int("receptiveness"), o.Int("diligence"), o.Int("selflessness"));
        var legacy = Legacy(o);
        var pp = Defaults.Profile(legacy);
        var diligence = pp.Diligence;
        if (o.Has("personalityFactor"))
            diligence = DiligenceFromFactor(o.Double("personalityFactor"), legacy, Defaults);
        return (pp.Receptiveness, diligence, pp.Selflessness);
    }

    /// <summary>
    /// 旧 PersonalityFactor → 勤勉さ（<see cref="PersonalityCoefficients.SelfGrowthFactor"/> の逆関数）。
    /// 旧タイプが Normal（中立）なら 50 のまま（手組み・未設定の安全弁を保つ）。
    /// </summary>
    internal static int DiligenceFromFactor(double factor, Personality legacy, PersonalityCoefficients pc)
    {
        if (legacy == Personality.Normal) return Personalities.AxisNeutral;
        var delta = factor - 1.0;
        var d = delta >= 0 ? delta / pc.DiligenceSlopeHigh : delta / pc.DiligenceSlopeLow;
        var v = (int)MathUtil.Clamp(Math.Round(Personalities.AxisNeutral + d * 50.0), 1, 99);
        // 旧タイプのマスの側を保つ（またいだらマスの側の端で止める）。
        return Personalities.CellHigh(legacy, PersonalityAxis.Diligence)
            ? Math.Max(v, Personalities.AxisThreshold)
            : Math.Min(v, Personalities.AxisThreshold - 1);
    }

    private static Personality Legacy(JsonObject o)
        => o.Has("personality") ? EnumJson.Read<Personality>(o.Str("personality")) : Personality.Normal;
}

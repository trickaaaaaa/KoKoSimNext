using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// チームの結束の係数（設計書23 §3・issue #934。YAML駆動 data/coefficients.yaml cohesion:）。
/// 結束 U（0〜100・50がふつう）＝顔ぶれの土台 U0 ＋出来事による上下 ΔU。効き先は精神面だけ
/// （動揺の起きにくさ・伝令の効き）で、能力そのものには効かせない。U=50 で効き先はすべて恒等。
/// </summary>
public sealed record CohesionCoefficients
{
    // ---- U0（顔ぶれの土台）。OPEN-QUESTIONS Q54 決着（2026-11-14）: 各項を「ふつうの部」の値からの差で測る形にし、
    //      顔ぶれだけで U0≥70 の部が上位約15%・U0<30 の部が下位約15% になるよう校正した（AI校の母集団で実測）。
    //      基準値（*Neutral）は全国の AI校のベンチ入りメンバーの平均＝ふつうの部で各項が0、U0≈50。

    /// <summary>U0: 主将の統率力（統率傾向×精神力/100）が基準を上回る1あたりの上乗せ b1（下回れば減点）。主将不在は0。</summary>
    public double CaptainPowerWeight { get; init; } = 0.9;
    /// <summary>U0: 主将の統率力の基準（ふつうの部の主将）。</summary>
    public double CaptainPowerNeutral { get; init; } = 46.0;
    /// <summary>U0: 「引っ張る」選手（統率傾向がこの値以上）の閾値（design-23 §3.2 「①が65以上」）。</summary>
    public int LeaderThreshold { get; init; } = 65;
    /// <summary>U0: 引っ張る選手の割合（ベンチ入りに占める）が基準を上回る分に掛ける上乗せ b2（下回れば減点）。</summary>
    public double LeaderShareWeight { get; init; } = 80.0;
    /// <summary>U0: 引っ張る選手の割合の基準（ふつうの部）。</summary>
    public double LeaderShareNeutral { get; init; } = 0.24;
    /// <summary>U0: 目立ちたがり（④&lt;50）の割合が基準を上回る分に掛ける減点 b3（下回れば上乗せ）。</summary>
    public double SelfishShareWeight { get; init; } = 70.0;
    /// <summary>U0: 目立ちたがりの割合の基準（ふつうの部）。</summary>
    public double SelfishShareNeutral { get; init; } = 0.385;
    /// <summary>U0: 一匹狼（①↓②↓③↓④目立ち）の割合が基準を上回る分に掛ける減点 b4（下回れば上乗せ）。</summary>
    public double LoneWolfShareWeight { get; init; } = 120.0;
    /// <summary>U0: 一匹狼の割合の基準（ふつうの部）。</summary>
    public double LoneWolfShareNeutral { get; init; } = 0.06;

    /// <summary>ΔU: 夏合宿（通常）の上乗せ（design-04 §4「結束↑」）。</summary>
    public double SummerCampDelta { get; init; } = 6.0;
    /// <summary>ΔU: 冬合宿（強化）の上乗せ（「結束↑↑」）。</summary>
    public double WinterCampDelta { get; init; } = 10.0;
    /// <summary>ΔU: 選手間イベントが好転したとき（不和解消など）。</summary>
    public double EventImproveDelta { get; init; } = 4.0;
    /// <summary>ΔU: 不和・サボりイベントが悪化したとき。</summary>
    public double EventWorsenDelta { get; init; } = -4.0;
    /// <summary>ΔU が週ごとに0へ戻る割合（10%）。</summary>
    public double WeeklyDecay { get; init; } = 0.10;

    /// <summary>効き先: U がこの値以上なら動揺の発生閾値（連続出塁数）+1。</summary>
    public double HighThreshold { get; init; } = 70.0;
    /// <summary>効き先: U がこの値未満なら動揺の発生閾値 −1。</summary>
    public double LowThreshold { get; init; } = 30.0;
    /// <summary>効き先: 伝令の効き 1 + c1×(U−50)/50 の c1（U=80 で+6%）。</summary>
    public double MessengerPerCohesion { get; init; } = 0.10;
}

/// <summary>
/// チームの結束（設計書23 §3）。純関数・乱数不使用。「チームの勢い」（#863・試合の結果で上がる）とは別物で、
/// 結束は人の顔ぶれと出来事で決まる＝二重に効かせない（勢いは打撃・制球の倍率、結束は動揺と伝令だけ）。
/// </summary>
public static class Cohesion
{
    public const double Neutral = 50.0;

    /// <summary>
    /// 顔ぶれの土台 U0（0〜100）。<paramref name="members"/>＝ベンチ入りの部員（試合では打順＋控え）。
    /// 主将の統率力は design-09 §8 の 統率傾向×精神力/100。
    /// <code>
    /// U0 = 50 + b1×(主将の統率力 − 基準)
    ///         + b2×(引っ張る選手の割合 − 基準) − b3×(目立ちたがりの割合 − 基準) − b4×(一匹狼の割合 − 基準)
    /// </code>
    /// 割合の分母はベンチ入り全員。性格を持たない手組みの選手（<see cref="Personality.Normal"/>）は顔ぶれに数えない＝
    /// どの項も基準どおりとみなす（主将が手組みなら主将の項も0）。全員が手組みのチームは 50 ちょうど（Q57）。
    /// </summary>
    public static double BaseValue(IReadOnlyCollection<Player> members, Player? captain, CohesionCoefficients c)
    {
        if (members.Count == 0) return Neutral;
        var u = Neutral;
        if (captain is not null && captain.Personality != Personality.Normal)
            u += c.CaptainPowerWeight * (captain.Leadership * captain.Mental / 100.0 - c.CaptainPowerNeutral);
        double leaders = 0, selfish = 0, loneWolves = 0;
        foreach (var p in members)
        {
            if (p.Personality == Personality.Normal) continue;   // 手組みの無補正の選手は顔ぶれに数えない（Q57）
            leaders += (p.Leadership >= c.LeaderThreshold ? 1 : 0) - c.LeaderShareNeutral;
            selfish += (Personalities.High(p.Selflessness) ? 0 : 1) - c.SelfishShareNeutral;
            loneWolves += (p.Personality == Personality.LoneWolf ? 1 : 0) - c.LoneWolfShareNeutral;
        }
        var n = (double)members.Count;
        u += c.LeaderShareWeight * leaders / n;
        u -= c.SelfishShareWeight * selfish / n;
        u -= c.LoneWolfShareWeight * loneWolves / n;
        return MathUtil.Clamp(u, 0.0, 100.0);
    }

    /// <summary>結束 U ＝ U0 ＋ ΔU（0〜100）。</summary>
    public static double Value(double baseValue, double delta) => MathUtil.Clamp(baseValue + delta, 0.0, 100.0);

    /// <summary>効き先①: 動揺の発生閾値の段（U≥高 で+1、U&lt;低 で−1、それ以外0）。</summary>
    public static int RattledThresholdStep(double u, CohesionCoefficients c)
        => u >= c.HighThreshold ? 1 : u < c.LowThreshold ? -1 : 0;

    /// <summary>効き先②: 伝令の効きの倍率 1 + c1×(U−50)/50（U=50 で1）。</summary>
    public static double MessengerMultiplier(double u, CohesionCoefficients c)
        => 1.0 + c.MessengerPerCohesion * (u - Neutral) / 50.0;

    // ===== ΔU（自校だけ。AI校は常に0＝Q40） =====

    /// <summary>週ごとの減衰（10%ずつ0へ戻す）。</summary>
    public static double DecayWeek(double delta, CohesionCoefficients c) => delta * (1.0 - c.WeeklyDecay);

    /// <summary>合宿（夏＝通常／冬＝強化）。</summary>
    public static double AfterCamp(double delta, bool winter, CohesionCoefficients c)
        => delta + (winter ? c.WinterCampDelta : c.SummerCampDelta);

    /// <summary>選手間イベントの好転／悪化。</summary>
    public static double AfterEvent(double delta, bool improved, CohesionCoefficients c)
        => delta + (improved ? c.EventImproveDelta : c.EventWorsenDelta);

    /// <summary>
    /// 1週ぶん進める（自校の週送りが呼ぶ単一の入口）: まず 10% 戻し、合宿の週なら上乗せする
    /// （夏合宿＝通常・冬合宿＝強化。週は <see cref="Season.SeasonCalendar"/> の合宿週）。
    /// </summary>
    public static double AdvanceWeek(double delta, int week, Season.SeasonCalendar calendar, CohesionCoefficients c)
    {
        var d = DecayWeek(delta, c);
        if (week == calendar.SummerCampWeek) d = AfterCamp(d, winter: false, c);
        else if (week == calendar.WinterCampWeek) d = AfterCamp(d, winter: true, c);
        return ClampDelta(d);
    }

    /// <summary>ΔU が取りうる範囲（U が 0〜100 を外れない幅）に丸める。</summary>
    public static double ClampDelta(double delta) => MathUtil.Clamp(delta, -100.0, 100.0);
}

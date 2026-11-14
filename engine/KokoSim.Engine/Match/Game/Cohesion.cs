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
    /// <summary>U0: 主将の統率力（統率傾向×精神力/100＝0〜100）1あたりの上乗せ b1。主将不在は0。</summary>
    public double CaptainPowerWeight { get; init; } = 0.15;
    /// <summary>U0: 「引っ張る」選手（統率傾向がこの値以上）の人数を数える閾値（design-23 §3.2 「①が65以上」）。</summary>
    public int LeaderThreshold { get; init; } = 65;
    /// <summary>U0: 引っ張る選手1人あたりの上乗せ b2。</summary>
    public double LeaderWeight { get; init; } = 2.0;
    /// <summary>U0: 引っ張る選手を数える上限（3人まで）。</summary>
    public int LeaderCap { get; init; } = 3;
    /// <summary>U0: 目立ちたがり（④&lt;50）の割合が0.5を超えた分に掛ける減点 b3。</summary>
    public double SelfishShareWeight { get; init; } = 40.0;
    /// <summary>U0: 一匹狼（①↓②↓④目立ち）1人あたりの減点 b4。</summary>
    public double LoneWolfWeight { get; init; } = 3.0;

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
    /// </summary>
    public static double BaseValue(IReadOnlyCollection<Player> members, Player? captain, CohesionCoefficients c)
    {
        if (members.Count == 0) return Neutral;
        var u = Neutral;
        if (captain is not null) u += c.CaptainPowerWeight * (captain.Leadership * captain.Mental / 100.0);
        int leaders = 0, selfish = 0, loneWolves = 0;
        foreach (var p in members)
        {
            if (p.Leadership >= c.LeaderThreshold) leaders++;
            if (p.Personality == Personality.Normal) continue;   // 手組みの無補正の選手は顔ぶれの減点に数えない
            if (!Personalities.High(p.Selflessness)) selfish++;
            if (p.Personality == Personality.LoneWolf) loneWolves++;
        }
        u += c.LeaderWeight * Math.Min(leaders, c.LeaderCap);
        u -= c.SelfishShareWeight * Math.Max(0.0, (double)selfish / members.Count - 0.5);
        u -= c.LoneWolfWeight * loneWolves;
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

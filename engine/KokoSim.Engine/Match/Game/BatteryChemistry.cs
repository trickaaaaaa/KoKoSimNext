using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// バッテリーの相性の係数（設計書23 §2・issue #933。YAML駆動 data/coefficients.yaml battery_chemistry:）。
/// 相性 K = wS × S0 + wB × B。S0＝2人の性格からの出発点、B＝一緒に守ったイニングの積み重ね。
/// K=0 のとき効き先はすべて恒等（テストで固定）。
/// </summary>
public sealed record BatteryChemistryCoefficients
{
    /// <summary>S0: 捕手の統率（d①）の重み。捕手が引っ張るほど誰と組んでも合わせやすい。</summary>
    public double LeadershipWeight { get; init; } = 0.35;
    /// <summary>S0: 投手の素直さ（d②）の重み。素直な投手ほど捕手のリードを受け入れる。</summary>
    public double ReceptivenessWeight { get; init; } = 0.35;
    /// <summary>S0: 2人とも目立ちたがり＝ぶつかる（負）の重み。</summary>
    public double ClashWeight { get; init; } = 0.30;
    /// <summary>S0: 目立ちたがりの投手×献身的な捕手＝女房役（正）の重み。</summary>
    public double WifeRoleWeight { get; init; } = 0.30;

    /// <summary>K の出発点の重み wS。</summary>
    public double StartWeight { get; init; } = 0.5;
    /// <summary>K の積み重ねの重み wB。</summary>
    public double BondWeight { get; init; } = 0.5;
    /// <summary>B = 1 − exp(−イニング / τ) の τ（イニング。エース級が1シーズン組んで0.7前後）。</summary>
    public double BondTauInnings { get; init; } = 60.0;
    /// <summary>自校の記録: 公式戦の1イニングの重み（Q38）。</summary>
    public double OfficialInningWeight { get; init; } = 1.0;
    /// <summary>自校の記録: 練習試合・紅白戦の1イニングの重み（Q38）。</summary>
    public double PracticeInningWeight { get; init; } = 0.5;
    /// <summary>AI校の近似（Q39）: 2人が同じ学校で過ごした1年を何イニングぶんと見なすか。</summary>
    public double AiInningsPerYear { get; init; } = 30.0;

    /// <summary>効き先①: 捕手のリードの効きに掛ける 1 + k1 × K の k1。</summary>
    public double LeadFactorPerK { get; init; } = 0.30;
    /// <summary>効き先②: K がこの値以上なら動揺からの自然回復に要るアウトを1つ減らす。</summary>
    public double RattledRecoveryMinK { get; init; } = 0.6;
    /// <summary>効き先③: ピンチの制球 1 + k2 × K × (圧/最大圧) の k2（Q52 決着: 相性の良し悪しで1試合 ±0.1〜0.2点に届く大きさ）。</summary>
    public double PinchControlPerK { get; init; } = 0.20;
    /// <summary>効き先③を効かせるプレッシャー指数の下限（これ未満の場面では効かない＝「ピンチだけ」）。</summary>
    public int PinchPressureFrom { get; init; } = 2;
}

/// <summary>
/// バッテリーの相性（設計書23 §2）。純関数・乱数不使用。効き先は既存の解決式を通すものだけ
/// （捕手のリード／動揺の自然回復／ピンチの制球）＝表示能力を直接確率に使わない（不変条件#1）。
/// </summary>
public static class BatteryChemistry
{
    /// <summary>出発点の相性 S0（−1〜+1）。</summary>
    public static double StartingAffinity(Player pitcher, Player catcher, BatteryChemistryCoefficients c)
    {
        var dC1 = PersonalityCoefficients.D(catcher.Leadership);
        var dP2 = PersonalityCoefficients.D(pitcher.Receptiveness);
        var dP4 = PersonalityCoefficients.D(pitcher.Selflessness);
        var dC4 = PersonalityCoefficients.D(catcher.Selflessness);
        var s = c.LeadershipWeight * dC1
                + c.ReceptivenessWeight * dP2
                - c.ClashWeight * Math.Min(dP4, 0) * Math.Min(dC4, 0)
                + c.WifeRoleWeight * Math.Max(-dP4, 0) * Math.Max(dC4, 0);
        return MathUtil.Clamp(s, -1.0, 1.0);
    }

    /// <summary>積み重ね B（0〜1）。一緒に守ったイニング数（重み付き）から。</summary>
    public static double Bond(double innings, BatteryChemistryCoefficients c)
        => innings <= 0 || c.BondTauInnings <= 0 ? 0.0 : 1.0 - Math.Exp(-innings / c.BondTauInnings);

    /// <summary>AI校の積み重ね（Q39）: 2人が同じ学校で過ごした年数＝学年の小さい方（学年未設定は0）。</summary>
    public static double YearsTogether(Player pitcher, Player catcher)
        => Math.Max(0, Math.Min(pitcher.Grade, catcher.Grade));

    /// <summary>相性 K。</summary>
    public static double K(double startingAffinity, double bond, BatteryChemistryCoefficients c)
        => c.StartWeight * startingAffinity + c.BondWeight * bond;

    /// <summary>
    /// 試合中の相性 K。<paramref name="innings"/> が null（記録を持たない＝AI校・統計シム）なら、
    /// 同じ学校の年数から B を近似する。同一人物（捕手が投げている等）や捕手不在は0（恒等）。
    /// </summary>
    public static double For(Player pitcher, Player? catcher, double? innings, BatteryChemistryCoefficients c)
    {
        if (catcher is null || ReferenceEquals(pitcher, catcher)) return 0.0;
        var s0 = StartingAffinity(pitcher, catcher, c);
        var b = Bond(innings ?? YearsTogether(pitcher, catcher) * c.AiInningsPerYear, c);
        return K(s0, b, c);
    }

    /// <summary>効き先①: 捕手の実効リード（Lv）に相性を掛ける。K=0 で恒等。</summary>
    public static int ApplyToLead(int catcherLead, double k, BatteryChemistryCoefficients c)
    {
        if (k == 0.0) return catcherLead;
        return (int)MathUtil.Clamp(Math.Round(catcherLead * (1.0 + c.LeadFactorPerK * k)), 1, 100);
    }

    /// <summary>効き先②: 動揺からの自然回復に要るアウト数。K が閾値以上なら1つ減らす（1未満にしない）。</summary>
    public static int RecoveryOuts(int baseRecoveryOuts, double k, BatteryChemistryCoefficients c)
        => k >= c.RattledRecoveryMinK ? Math.Max(1, baseRecoveryOuts - 1) : baseRecoveryOuts;

    /// <summary>
    /// 効き先③: ピンチの制球倍率。プレッシャー指数が <see cref="BatteryChemistryCoefficients.PinchPressureFrom"/> 以上の場面だけ
    /// 1 + k2 × K × (指数/最大指数)。それ以外・K=0 は 1。
    /// </summary>
    public static double PinchControlMultiplier(double k, int pressureIndex, int maxPressureIndex,
        BatteryChemistryCoefficients c)
    {
        if (k == 0.0 || pressureIndex < c.PinchPressureFrom || maxPressureIndex <= 0) return 1.0;
        var strength = Math.Min(1.0, (double)pressureIndex / maxPressureIndex);
        return 1.0 + c.PinchControlPerK * k * strength;
    }
}

using KokoSim.Engine.Core;

namespace KokoSim.Engine.Players;

/// <summary>
/// 球質タイプ（設計書01 §1.1b「技巧派投手」/ 設計書02 §2「本格派はキレで押し込む・技巧派は制球で散らす」）。
/// <b>投手総合力は変えず、球速・制球・スタミナ・キレの配分だけを変える</b>ための型。
/// これにより「球速＝投手の良さ」ではなくなり、同じ総合ランクに本格派と技巧派が併存する。
/// </summary>
public enum PitcherArchetype
{
    /// <summary>バランス型（型なし）。</summary>
    Balanced,
    /// <summary>本格派: 球速とキレで押し込む。制球はやや粗い。</summary>
    Power,
    /// <summary>技巧派: 球速は平凡だが制球とキレで打たせて取る。</summary>
    Finesse,
    /// <summary>軟投派: 球速を捨て、抜群の制球と変化球で幻惑する。</summary>
    SoftToss,
}

/// <summary>
/// 球質タイプの出現割合と、型ごとのレベル配分オフセット（data/coefficients.yaml 駆動・不変条件#4）。
/// オフセットは<b>実際の試合の失点が型によらず揃うよう実測で校正</b>してある（式の上での加重和ではない）。
/// <para>実測の限界失点（baseLv68・自校固定打線・YAML係数）: 球速 −0.015/Lv・制球 −0.057/Lv・キレ −0.009/Lv。
/// 球速の空振り価値は物理層接続（<see cref="Match.Batting.BattingCoefficients.StuffPerKmh"/>）で3倍になったが、
/// 与四球経由で効く制球の失点価値は依然その約4倍あるため、制球の振れ幅は±5程度が中立の上限。
/// 本格派は設計書02「球速とキレで押し込む」に合わせてキレを+に戻し、その分の代償を制球で払う配分にしてある。</para>
/// </summary>
public sealed record PitcherArchetypeCoefficients
{
    // --- 出現割合（残りが Balanced） ---
    public double PowerShare { get; init; } = 0.25;
    public double FinesseShare { get; init; } = 0.22;
    public double SoftTossShare { get; init; } = 0.08;

    // --- 本格派: 球速↑・キレ↑（代償は制球とスタミナ） ---
    public double PowerVelocity { get; init; } = 12;
    public double PowerControl { get; init; } = -4;
    public double PowerStamina { get; init; } = -2;
    public double PowerPitchRank { get; init; } = 2;

    // --- 技巧派: 球速↓ 制球・キレ↑ ---
    public double FinesseVelocity { get; init; } = -10;
    public double FinesseControl { get; init; } = 3;
    public double FinesseStamina { get; init; } = 1;
    public double FinessePitchRank { get; init; } = 3;

    // --- 軟投派: 球速↓↓ 制球・キレ↑（上乗せは Issue #24 の打球変更後に実戦で再校正済み） ---
    public double SoftTossVelocity { get; init; } = -14;
    public double SoftTossControl { get; init; } = 3;
    public double SoftTossStamina { get; init; } = 2;
    public double SoftTossPitchRank { get; init; } = 3;
}

/// <summary>球質タイプの抽選・オフセット適用・表示名（純関数）。</summary>
public static class PitcherArchetypes
{
    /// <summary>
    /// 球質タイプを抽選する。呼び出し側は Fork した独立ストリームを渡すこと
    /// （既存の能力ロール列を乱さないため・不変条件#2）。
    /// </summary>
    public static PitcherArchetype Sample(IRandomSource rng, PitcherArchetypeCoefficients c)
    {
        var r = rng.NextDouble();
        if (r < c.PowerShare) return PitcherArchetype.Power;
        r -= c.PowerShare;
        if (r < c.FinesseShare) return PitcherArchetype.Finesse;
        r -= c.FinesseShare;
        return r < c.SoftTossShare ? PitcherArchetype.SoftToss : PitcherArchetype.Balanced;
    }

    /// <summary>型ごとのレベル配分オフセット（球速/制球/スタミナ/キレ）。</summary>
    public static (double Velocity, double Control, double Stamina, double PitchRank) Offsets(
        PitcherArchetype a, PitcherArchetypeCoefficients c) => a switch
    {
        PitcherArchetype.Power => (c.PowerVelocity, c.PowerControl, c.PowerStamina, c.PowerPitchRank),
        PitcherArchetype.Finesse => (c.FinesseVelocity, c.FinesseControl, c.FinesseStamina, c.FinessePitchRank),
        PitcherArchetype.SoftToss => (c.SoftTossVelocity, c.SoftTossControl, c.SoftTossStamina, c.SoftTossPitchRank),
        _ => (0, 0, 0, 0),
    };

    /// <summary>表示名（展望の寸評・選手詳細で使う）。</summary>
    public static string Label(PitcherArchetype a) => a switch
    {
        PitcherArchetype.Power => "本格派",
        PitcherArchetype.Finesse => "技巧派",
        PitcherArchetype.SoftToss => "軟投派",
        _ => "バランス型",
    };

    /// <summary>
    /// 能力レベルから球質タイプを推定する（生成時の型を保持していない投手＝既存データ・投影後の Player 用）。
    /// 球速と制球の乖離で判定する。表示のためだけに使い、生成の型と厳密一致は求めない。
    /// </summary>
    public static PitcherArchetype Infer(int velocityLevel, int controlLevel, int pitchRankLevel)
    {
        var gap = controlLevel - velocityLevel;
        if (gap >= 18) return PitcherArchetype.SoftToss;
        if (gap >= 7) return PitcherArchetype.Finesse;
        if (gap <= -7) return PitcherArchetype.Power;
        return PitcherArchetype.Balanced;
    }
}

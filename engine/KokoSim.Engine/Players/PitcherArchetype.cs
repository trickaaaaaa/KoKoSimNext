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
/// 本格派は設計書02「球速とキレで押し込む」に合わせてキレを+に保ち、その分の代償を制球で払う配分にしてある。</para>
///
/// <para><b>issue #578 Step 2/3 の再校正（2026-08-10）</b>: 球速の空振り価値を2倍にした
/// （<see cref="Match.Batting.BattingCoefficients.StuffPerKmh"/> 0.064→0.128・オーナー方針 Q24-1）ため、
/// 旧オフセット（球速+12/−10/−14）のままでは本格派が一方的に有利になり
/// <c>Archetypes_AreBalanceNeutral_InActualGames</c> が割れた（実測 本格2.33 / 軟投3.48・許容0.50）。
/// <b>代償を制球・スタミナ・キレへ移しても直らない</b>のが実測の要点で:
/// 制球は4Lvで +0.17 しか動かず、軟投派に至っては制球を上げると**失点が増える**
/// （打たせて取る型は制球が良いほどインプレーが増え、失策失点が乗る）。スタミナを下げても
/// 早めの継投で相殺される。よって<b>球速オフセット自体を価値の上昇ぶん縮める</b>のが唯一効くレバーで、
/// 8/−7/−10 へ寝かせた（本格派−技巧派は 15Lv≒7.0km/h ＝ 型の見分けは残る）。</para>
/// </summary>
public sealed record PitcherArchetypeCoefficients
{
    // --- 出現割合（残りが Balanced） ---
    public double PowerShare { get; init; } = 0.25;
    public double FinesseShare { get; init; } = 0.22;
    public double SoftTossShare { get; init; } = 0.08;

    // --- 本格派: 球速↑・キレ↑（代償は制球とスタミナ） ---
    public double PowerVelocity { get; init; } = 10;
    public double PowerControl { get; init; } = -4;   // 2026-08-16: −12→−7 ／ 2026-08-18: −7→−4・球速 8→10（投手の打球品質寄与×3 で制球の価値がさらに上がった分。issue pitcher-barely-affects-contact-quality）
    public double PowerStamina { get; init; } = -2;
    public double PowerPitchRank { get; init; } = 2;

    // --- 技巧派: 球速↓ 制球・キレ↑ ---
    public double FinesseVelocity { get; init; } = -8;
    public double FinesseControl { get; init; } = 3;
    public double FinesseStamina { get; init; } = 1;
    public double FinessePitchRank { get; init; } = 2;

    // --- 軟投派: 球速↓↓ 制球・キレ↑（上乗せは Issue #24 の打球変更後に実戦で再校正済み） ---
    public double SoftTossVelocity { get; init; } = -11;
    public double SoftTossControl { get; init; } = 2;
    public double SoftTossStamina { get; init; } = 2;
    public double SoftTossPitchRank { get; init; } = 2;
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

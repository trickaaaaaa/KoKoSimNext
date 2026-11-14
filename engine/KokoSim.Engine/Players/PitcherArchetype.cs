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
    // --- 尖った型（issue #908・各3%）: 平均の失点は他の型と揃え、試合ごとの振れ幅は大きくてよい ---
    /// <summary>剛腕: 全国屈指の球速だが制球は壊滅的（四球も三振も多く試合が荒れる）。</summary>
    Flamethrower,
    /// <summary>精密機械: 球は遅いが四球をほとんど出さない。</summary>
    Pinpoint,
    /// <summary>変化球の魔術師: 球は遅いが決め球のキレが突出している。</summary>
    BreakingBallArtist,
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
    public double FlamethrowerShare { get; init; } = 0.03;
    public double PinpointShare { get; init; } = 0.03;
    public double BreakingBallArtistShare { get; init; } = 0.03;

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

    // --- 剛腕: 全国屈指の球速だが制球は壊滅的（四球も三振も多く試合が荒れる）（issue #908。実戦の失点が他の型と揃うよう実測で校正） ---
    public double FlamethrowerVelocity { get; init; } = 30;
    /// <summary>
    /// 剛腕の制球は足し算でなく<b>割合</b>で決める（issue #908・オーナー決定 2026-11-14「1＋2」）: 制球＝地力×これ＋<see cref="FlamethrowerControl"/>。
    /// 足し算（旧 −22）だと強豪校は地力が上限 99 に溢れて制球77（B）になり「荒れ球」にならなかった。割合なら学校の水準に
    /// 連動しつつ、いつも「その学校で一番荒れている」投手になる（地力45→G・68→F・99→D）。成長も地力側に足してから掛け直すので、
    /// 溢れた分は制球に回らない（<see cref="PitcherArchetypes.Apply"/>）。
    /// </summary>
    public double FlamethrowerControlFactor { get; init; } = 0.55;
    public double FlamethrowerControl { get; init; } = 0;
    public double FlamethrowerStamina { get; init; } = 0;
    public double FlamethrowerPitchRank { get; init; } = 30;

    // --- 精密機械: 球は遅いが四球をほとんど出さない（issue #908。実戦の失点が他の型と揃うよう実測で校正） ---
    public double PinpointVelocity { get; init; } = -24;
    public double PinpointControl { get; init; } = 12;
    public double PinpointStamina { get; init; } = 2;
    public double PinpointPitchRank { get; init; } = -4;

    // --- 変化球の魔術師: 球は遅いが決め球のキレが突出している（issue #908。実戦の失点が他の型と揃うよう実測で校正） ---
    public double BreakingBallArtistVelocity { get; init; } = -8;
    public double BreakingBallArtistControl { get; init; } = -4;
    public double BreakingBallArtistStamina { get; init; } = 0;
    public double BreakingBallArtistPitchRank { get; init; } = 20;
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
        if (r < c.SoftTossShare) return PitcherArchetype.SoftToss;
        r -= c.SoftTossShare;
        if (r < c.FlamethrowerShare) return PitcherArchetype.Flamethrower;
        r -= c.FlamethrowerShare;
        if (r < c.PinpointShare) return PitcherArchetype.Pinpoint;
        r -= c.PinpointShare;
        return r < c.BreakingBallArtistShare ? PitcherArchetype.BreakingBallArtist : PitcherArchetype.Balanced;
    }

    /// <summary>型ごとのレベル配分オフセット（球速/制球/スタミナ/キレ）。</summary>
    public static (double Velocity, double Control, double Stamina, double PitchRank) Offsets(
        PitcherArchetype a, PitcherArchetypeCoefficients c) => a switch
    {
        PitcherArchetype.Power => (c.PowerVelocity, c.PowerControl, c.PowerStamina, c.PowerPitchRank),
        PitcherArchetype.Finesse => (c.FinesseVelocity, c.FinesseControl, c.FinesseStamina, c.FinessePitchRank),
        PitcherArchetype.SoftToss => (c.SoftTossVelocity, c.SoftTossControl, c.SoftTossStamina, c.SoftTossPitchRank),
        PitcherArchetype.Flamethrower => (c.FlamethrowerVelocity, c.FlamethrowerControl, c.FlamethrowerStamina, c.FlamethrowerPitchRank),
        PitcherArchetype.Pinpoint => (c.PinpointVelocity, c.PinpointControl, c.PinpointStamina, c.PinpointPitchRank),
        PitcherArchetype.BreakingBallArtist => (c.BreakingBallArtistVelocity, c.BreakingBallArtistControl, c.BreakingBallArtistStamina, c.BreakingBallArtistPitchRank),
        _ => (0, 0, 0, 0),
    };

    /// <summary>
    /// 型の上乗せ（球速/制球/スタミナ/キレ）の合計。投手の4能力の合計からこれを差し引くと、型によらない地力の尺度になる。
    /// 上乗せは「試合の失点が型によらず揃う」よう実測で校正した値で、合計は揃っていない（本格派 +6・技巧派 −2・軟投派 −5）。
    /// 4能力をそのまま足して投手を比べると、同じ実力でも本格派が上に来る（issue #908）。型が無い（null）投手は 0。
    /// </summary>
    public static double OffsetSum(PitcherArchetype? a, PitcherArchetypeCoefficients c)
    {
        if (a is not { } type) return 0.0;
        var (v, ctl, st, r) = Offsets(type, c);
        return v + ctl + st + r;
    }

    /// <summary>制球の割合（剛腕だけ 1 未満・ほかの型は 1＝割合を掛けない）。</summary>
    public static double ControlFactor(PitcherArchetype? a, PitcherArchetypeCoefficients c)
        => a == PitcherArchetype.Flamethrower ? c.FlamethrowerControlFactor : 1.0;

    /// <summary>
    /// 地力（型を当てる前の4能力の Level）に型を当てる。制球は 地力×割合＋上乗せ、ほかは 地力＋上乗せ。
    /// 丸め・切り詰めは呼び出し側（AI校は球速だけ天井、ほかは 10〜99）。生成と毎年の成長が同じ式を通る（単一ソース）。
    /// </summary>
    public static (double Velocity, double Control, double Stamina, double PitchRank) Apply(
        PitcherArchetype? a, double velocity, double control, double stamina, double pitchRank, PitcherArchetypeCoefficients c)
    {
        if (a is not { } type) return (velocity, control, stamina, pitchRank);
        var (dv, dc, ds, dr) = Offsets(type, c);
        return (velocity + dv, control * ControlFactor(type, c) + dc, stamina + ds, pitchRank + dr);
    }

    /// <summary><see cref="Apply"/> の制球だけの逆（成長を地力側に足すため）。</summary>
    public static double BaseControl(PitcherArchetype? a, double control, PitcherArchetypeCoefficients c)
    {
        if (a is not { } type) return control;
        var (_, dc, _, _) = Offsets(type, c);
        return (control - dc) / ControlFactor(type, c);
    }

    /// <summary>
    /// 投手を比べるための地力の合計＝4能力から型の影響を外した和（issue #908）。エース選び・継投の人選が使う。
    /// 型の上乗せは試合の失点が型によらず揃うよう校正してあり合計は揃っていないので、素の合計で並べると型で選り好みが起きる。
    /// </summary>
    public static double StrengthSum(PitcherArchetype? a, double velocity, double control, double stamina, double pitchRank,
        PitcherArchetypeCoefficients c)
    {
        if (a is not { } type) return velocity + control + stamina + pitchRank;
        var (dv, _, ds, dr) = Offsets(type, c);
        return velocity - dv + BaseControl(type, control, c) + stamina - ds + pitchRank - dr;
    }

    /// <summary>表示名（展望の寸評・選手詳細で使う）。</summary>
    public static string Label(PitcherArchetype a) => a switch
    {
        PitcherArchetype.Power => "本格派",
        PitcherArchetype.Finesse => "技巧派",
        PitcherArchetype.SoftToss => "軟投派",
        PitcherArchetype.Flamethrower => "剛腕",
        PitcherArchetype.Pinpoint => "精密機械",
        PitcherArchetype.BreakingBallArtist => "変化球の魔術師",
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

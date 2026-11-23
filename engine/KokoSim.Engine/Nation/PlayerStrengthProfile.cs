using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 選手個人のカテゴリ別ランク（打撃力/走力/守備力/投手力, Issue #30）。表示専用・帯不変。
/// チーム総合力（<see cref="TeamStrengthProfile"/>）と同じサブ合成式・同じ <see cref="TeamStrengthCoefficients"/> を
/// 流用し、二重管理にしない（2026-07-22 owner決定・候補A）。野手/投手を問わず常に4カテゴリを計算する
/// （候補A: 非該当側も生成時の余技能力値からそのまま算出し、多くはG相当に収まる）。
/// </summary>
public sealed record PlayerStrength(
    double Batting,
    double Mobility,
    double Defense,
    double Pitching)
{
    public Tier BattingTier => Tiers.FromStrength(Batting);
    public Tier MobilityTier => Tiers.FromStrength(Mobility);
    public Tier DefenseTier => Tiers.FromStrength(Defense);
    public Tier PitchingTier => Tiers.FromStrength(Pitching);
}

/// <summary>
/// 育成選手1名から <see cref="PlayerStrength"/> を集計する純関数（Unity非依存・決定論）。
/// </summary>
public static class PlayerStrengthProfile
{
    /// <param name="delta">表示Lvに足す量。画面のチップは表示Lvのまま（0＝issue #1080 で Δ 下がる）。ドラフトの足切りのように
    /// 今の物差しの境目で判定する所だけ <see cref="Players.AbilityScale.Delta"/> を渡す。</param>
    public static PlayerStrength Compute(DevelopingPlayer player, TeamStrengthCoefficients c, int delta = 0) => new(
        Batting: TeamStrengthProfile.BatterComposite(player, c, delta),
        Mobility: TeamStrengthProfile.MobilityComposite(player, c, delta),
        Defense: TeamStrengthProfile.DefenseComposite(player, c, delta),
        Pitching: TeamStrengthProfile.PitcherComposite(player, c, delta));
}

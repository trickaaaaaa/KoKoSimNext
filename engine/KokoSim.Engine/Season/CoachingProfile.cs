namespace KokoSim.Engine.Season;

/// <summary>
/// 監督の分野別指導力（設計書04 §1.1）を育成係数へ写像する注入値（Issue #115・OPEN-QUESTIONS Q7(b)）。
/// 打撃 / 投手 / 守備・走塁 の3分野それぞれの指導力を持ち、能力種別に応じて coachingFactor を作る:
///   投手系（球速/制球/スタミナ/キレ）← <see cref="Pitching"/>
///   守備・走塁系（守備/捕球/肩/送球精度/走力/バント/盗塁/走塁判断）← <see cref="Defense"/>
///   打撃系（それ以外＝ミート/パワー/弾道/選球眼）← <see cref="Batting"/>
///
/// coachingFactor = 1 + 指導力 × coaching_slope × 素直さ（従来式を分野別に展開しただけ）。
/// SeasonContext.Coaching が null の場合は <see cref="TrainingCoefficients.CoachingLevel"/>（既定20）を
/// 全分野に適用したのと同じ＝従来のシムと1ビットも変わらない（不変条件#2）。
/// </summary>
public sealed record CoachingProfile
{
    /// <summary>打撃系指導力（設計書04: Coaching.Batting）。</summary>
    public double Batting { get; init; }
    /// <summary>投手系指導力（設計書04: Coaching.Pitching）。</summary>
    public double Pitching { get; init; }
    /// <summary>守備・走塁系指導力（設計書04: Coaching.Defense）。</summary>
    public double Defense { get; init; }

    /// <summary>能力種別に対応する分野別指導力を返す。</summary>
    public double LevelFor(AbilityKind k)
    {
        if (AbilityKinds.IsPitching(k)) return Pitching;
        if (IsDefenseOrRunning(k)) return Defense;
        return Batting;
    }

    /// <summary>守備・走塁系（Defense 指導力の適用対象）。守備4能力＋走力＋走塁系3能力。</summary>
    internal static bool IsDefenseOrRunning(AbilityKind k) =>
        AbilityKinds.IsDefense(k)
        || k is AbilityKind.Speed or AbilityKind.Bunt or AbilityKind.Steal or AbilityKind.Baserunning;

    /// <summary>監督メタ（分野別指導力）から育成注入値を作る（Phase 5監督メタ ⇄ Phase 3育成の接続）。</summary>
    public static CoachingProfile FromManager(Career.Manager m) => new()
    {
        Batting = m.CoachingBatting,
        Pitching = m.CoachingPitching,
        Defense = m.CoachingDefense,
    };

    /// <summary>全分野を同一の指導力に据える（テスト・後方互換の基準用）。</summary>
    public static CoachingProfile Uniform(double level) => new()
    {
        Batting = level,
        Pitching = level,
        Defense = level,
    };
}

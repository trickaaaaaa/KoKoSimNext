namespace KokoSim.Engine.Season;

/// <summary>育成対象の能力種別（表示層, 1〜100）。</summary>
public enum AbilityKind
{
    // 野手
    Contact,
    Power,
    LaunchTendency,
    Discipline,
    Speed,
    ArmStrength,
    Fielding,
    Catching,
    // 投手
    Velocity,
    Control,
    Stamina,
    PitchRank,
    // 走塁系（設計書01 §1.2・設計書02 §4）: スキルから連続パラメータへ移行。末尾に追加し (int) 序列を不変に保つ。
    Bunt,         // バント: 犠打・セーフティの成功率
    Steal,        // 盗塁: スタートの速さ・盗塁成功率（走力とは別の判断・技術）
    Baserunning,  // 走塁判断: 進塁判断・本塁突入/自重の的確さ（実戦でのみ成長, §5.3a）
    ThrowAccuracy,// 送球精度: 肩(ArmStrength)から分離した送球の正確さ（§1.2）
}

public static class AbilityKinds
{
    public static readonly AbilityKind[] All = (AbilityKind[])Enum.GetValues(typeof(AbilityKind));
    public static readonly AbilityKind[] Batting =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.LaunchTendency, AbilityKind.Discipline,
        AbilityKind.Speed, AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.Catching,
    };
    public static readonly AbilityKind[] Pitching =
    {
        AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank,
    };

    /// <summary>走塁系連続パラメータ（設計書02 §4）。バント・盗塁・走塁判断。</summary>
    public static readonly AbilityKind[] Running =
    {
        AbilityKind.Bunt, AbilityKind.Steal, AbilityKind.Baserunning,
    };

    /// <summary>
    /// 旧12能力（野手8＋投手4）。生成器はこれを反復して従来の乱数消費列を保つ。
    /// 走塁系・送球精度は 2A では乱数を引かず派生/既定値で設定する（決定論の後方互換）。
    /// </summary>
    public static readonly AbilityKind[] CoreTwelve =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.LaunchTendency, AbilityKind.Discipline,
        AbilityKind.Speed, AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.Catching,
        AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank,
    };

    public static bool IsPitching(AbilityKind k) => k is AbilityKind.Velocity or AbilityKind.Control
        or AbilityKind.Stamina or AbilityKind.PitchRank;

    /// <summary>守備系（伸びしろ分野の判定に使用, 設計書01 §1.1）。</summary>
    public static bool IsDefense(AbilityKind k) => k is AbilityKind.Fielding or AbilityKind.Catching
        or AbilityKind.ArmStrength or AbilityKind.ThrowAccuracy;
}

using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Season;

/// <summary>練習メニュー（設計書03 §3.1）。</summary>
public enum TrainingMenu
{
    Batting,        // ミート打撃: 主効果=ミート、副効果=選球眼
    PowerHitting,   // 長打打撃: 主効果=パワー、副効果=弾道
    PlateDiscipline,// 選球練習: 主効果=選球眼、副効果=ミート
    Strength,     // 筋力トレ: 主効果=パワー、副効果=肩（筋肉バカ育成向け）
    BaseRunning,  // 走塁練習
    Defense,      // 守備練習（守備地力: 主効果=守備、副効果=捕球）
    Throwing,     // 遠投・送球
    Pitching,     // 投げ込み
    BreakingBall, // 変化球練習
    Running,      // ランニング
    VelocityTraining, // 球速強化（下半身・全力投球）: 主効果=球速のみ、副効果なし（コントロールは上げない）
    Bunt,         // バント練習: 主効果=バント、副効果=ミート（バットコントロール）
    // ポジション別守備練習（設計書01 §1.1 / 03 §3.1）: そのポジションの適性だけを伸ばす。
    DefenseP,     // 投手守備
    DefenseC,     // 捕手守備
    Defense1B,    // 一塁守備
    Defense2B,    // 二塁守備
    Defense3B,    // 三塁守備
    DefenseSS,    // 遊撃守備
    DefenseLF,    // 左翼守備
    DefenseCF,    // 中堅守備
    DefenseRF,    // 右翼守備
    DefenseInfield,  // 内野汎用守備: 内野4ポジの適性を薄く全上げ（ユーティリティ育成）
    DefenseOutfield, // 外野汎用守備: 外野3ポジの適性を薄く全上げ（ユーティリティ育成）
    Rest,         // 休養
}

/// <summary>
/// 練習メニューの効果（設計書03 §3.1）。能力値（Main/Subs）と守備位置適性（Aptitudes）の両レイヤーを持つ。
/// Aptitudes の Weight は主効果基礎値への倍率（1.0=専念、汎用は薄く）。
/// </summary>
public readonly record struct MenuEffect(
    AbilityKind? Main,
    AbilityKind[] Subs,
    (FieldPosition Pos, double Weight)[] Aptitudes);

public static class TrainingMenus
{
    private static readonly AbilityKind[] None = System.Array.Empty<AbilityKind>();
    private static readonly (FieldPosition, double)[] NoPos = System.Array.Empty<(FieldPosition, double)>();

    /// <summary>単一ポジション専念（適性のみ、その位置だけ伸びる）。</summary>
    private static MenuEffect Pos(FieldPosition p) =>
        new(null, None, new[] { (p, 1.0) });

    // 汎用守備の1ポジあたりの薄さ（複数ポジに分散するため主効果より控えめ）。
    private const double GeneralAptitudeWeight = 0.5;

    private static MenuEffect Group(params FieldPosition[] ps)
    {
        var arr = new (FieldPosition, double)[ps.Length];
        for (var i = 0; i < ps.Length; i++) arr[i] = (ps[i], GeneralAptitudeWeight);
        return new MenuEffect(null, None, arr);
    }

    /// <summary>メニュー→効果。副効果はそれぞれ sub_factor 倍で入る。休養は効果なし。設計書03 §3.1。</summary>
    public static MenuEffect Effects(TrainingMenu menu) => menu switch
    {
        TrainingMenu.Batting => new(AbilityKind.Contact, new[] { AbilityKind.Discipline }, NoPos),
        TrainingMenu.PowerHitting => new(AbilityKind.Power, new[] { AbilityKind.LaunchTendency }, NoPos),
        TrainingMenu.PlateDiscipline => new(AbilityKind.Discipline, new[] { AbilityKind.Contact }, NoPos),
        TrainingMenu.Strength => new(AbilityKind.Power, new[] { AbilityKind.ArmStrength }, NoPos),
        TrainingMenu.BaseRunning => new(AbilityKind.Speed, None, NoPos),
        TrainingMenu.Defense => new(AbilityKind.Fielding, new[] { AbilityKind.Catching }, NoPos),
        TrainingMenu.Throwing => new(AbilityKind.ArmStrength, new[] { AbilityKind.Fielding }, NoPos),
        TrainingMenu.Pitching => new(AbilityKind.Control, new[] { AbilityKind.Stamina }, NoPos),
        TrainingMenu.BreakingBall => new(AbilityKind.PitchRank, None, NoPos),
        TrainingMenu.Running => new(AbilityKind.Stamina, new[] { AbilityKind.Speed }, NoPos),
        // 球速主体。副効果はスタミナ・パワーを少し（sub_factor倍）。コントロールは上げない。
        TrainingMenu.VelocityTraining => new(AbilityKind.Velocity, new[] { AbilityKind.Stamina, AbilityKind.Power }, NoPos),
        // バット操作技術。副効果はミートを少し。
        TrainingMenu.Bunt => new(AbilityKind.Bunt, new[] { AbilityKind.Contact }, NoPos),
        // ポジション別守備: そのポジションの適性だけ伸びる。
        TrainingMenu.DefenseP => Pos(FieldPosition.Pitcher),
        TrainingMenu.DefenseC => Pos(FieldPosition.Catcher),
        TrainingMenu.Defense1B => Pos(FieldPosition.FirstBase),
        TrainingMenu.Defense2B => Pos(FieldPosition.SecondBase),
        TrainingMenu.Defense3B => Pos(FieldPosition.ThirdBase),
        TrainingMenu.DefenseSS => Pos(FieldPosition.Shortstop),
        TrainingMenu.DefenseLF => Pos(FieldPosition.LeftField),
        TrainingMenu.DefenseCF => Pos(FieldPosition.CenterField),
        TrainingMenu.DefenseRF => Pos(FieldPosition.RightField),
        // 汎用守備: グループ内の全ポジを薄く。ユーティリティ育成向け。
        TrainingMenu.DefenseInfield => Group(
            FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop),
        TrainingMenu.DefenseOutfield => Group(
            FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField),
        _ => new(null, None, NoPos), // Rest
    };
}

namespace KokoSim.Engine.Match.Batting;

/// <summary>
/// 打った打球の方向・質へのスキル補正（設計書10 §2.1・issue #907）。<see cref="ContactModel.Resolve"/> がフェア打球の
/// 生成で使う。既定（全0）は恒等＝スキルなしの打球と1ビットも変わらない。乱数は追加で引かない。
/// </summary>
/// <param name="OppositeFieldExitBonus">流し打ち: 逆方向へ飛んだ打球の初速の上乗せ率（0.04＝+4%）。</param>
/// <param name="OppositeFieldMinBearingDeg">流し打ち: 中堅からこれ以上逆方向へ寄った打球を「逆方向」とみなす[deg]。</param>
/// <param name="BearingShiftDeg">つなぎの打撃: 打球方向のずらし[deg]（＋＝右方向＝一塁側。打者の左右に依らない）。</param>
/// <param name="LaunchShiftDeg">つなぎの打撃: 打球角のずらし[deg]（負＝低く）。</param>
/// <param name="QualityShift">つなぎの打撃: コンタクト品質への加算（負＝弱い打球）。</param>
public readonly record struct BattedBallSkillMods(
    double OppositeFieldExitBonus,
    double OppositeFieldMinBearingDeg,
    double BearingShiftDeg,
    double LaunchShiftDeg,
    double QualityShift)
{
    public static BattedBallSkillMods None => default;

    public bool IsIdentity
        => OppositeFieldExitBonus == 0.0 && BearingShiftDeg == 0.0 && LaunchShiftDeg == 0.0 && QualityShift == 0.0;

    /// <summary>
    /// 打球方向[deg]（−＝左翼／＋＝右翼）が打者から見て逆方向か。右打者は＋側（右翼）、左打者・スイッチは−側（左翼）が逆方向
    /// （<see cref="ContactModel.BearingDeg"/> と同じ左右の規約）。
    /// </summary>
    public bool IsOppositeField(double bearingDeg, Players.Handedness bats)
    {
        var pullSign = bats is Players.Handedness.Left or Players.Handedness.Switch ? 1.0 : -1.0;
        return -pullSign * bearingDeg >= OppositeFieldMinBearingDeg;
    }
}

using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Field;

/// <summary>
/// 逸れた送球の回収レース（design-12 §3.8）。新しい確率式は作らず、既存の「守備所要時間 vs 走者所要時間」
/// の時間勝負へ、拾い直しのロスを守備側の所要時間に加算するだけで統合する。
/// 回収役は「受け手自身」（起点＝塁、逸れ幅ぶんだけ動く。拾って直接タッチ＝返球不要）と
/// 「バックアップ」（定位置から着弾点まで走り、塁へ返球する）の近い方（min）。
/// </summary>
public static class ThrowRecovery
{
    /// <summary>目標塁ごとのバックアップ野手（実際の野球のバックアップ規則: 一塁→右翼手／二塁→中堅手／
    /// 三塁→左翼手／本塁→投手）。フェーズ1で実際に呼ばれるのは一塁(1)と本塁(4)のみ（design-12 §3.8）。</summary>
    public static FieldPosition BackupRole(int targetBase) => targetBase switch
    {
        1 => FieldPosition.RightField,
        2 => FieldPosition.CenterField,
        3 => FieldPosition.LeftField,
        _ => FieldPosition.Pitcher,
    };

    /// <summary>送球が塁を通り過ぎて転がり着く点（塁から送球方向へ逸れ幅ぶん延長した点）。</summary>
    public static Vector3D LandingPoint(Vector3D origin, Vector3D target, double scatterM)
        => target + (target - origin).Normalized() * scatterM;

    /// <summary>回収ロス[s]。受け手自身の拾い直し（返球不要）とバックアップ（拾って返球）の近い方。</summary>
    public static double RecoveryPenaltySeconds(
        double scatterM, Vector3D landingPoint, Vector3D backupPosition,
        double selfSprintSpeedMps, double backupSprintSpeedMps, double backupThrowSpeedMps,
        double reactionSeconds)
    {
        var selfTime = scatterM / selfSprintSpeedMps + reactionSeconds;
        var backupTime = (backupPosition - landingPoint).Length / backupSprintSpeedMps
                          + reactionSeconds + scatterM / backupThrowSpeedMps;
        return System.Math.Min(selfTime, backupTime);
    }
}

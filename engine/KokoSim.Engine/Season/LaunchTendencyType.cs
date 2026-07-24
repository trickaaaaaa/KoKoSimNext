namespace KokoSim.Engine.Season;

/// <summary>
/// 弾道（LaunchTendency）のタイプ軸分類（設計書01 §1.2）。ゴロ型〜フライ型の5段。
/// 優劣軸ではないため Tier(G..S) とは別の分類にする（issue #219）。
/// </summary>
public enum LaunchTendencyType
{
    GroundBall,
    LeanGround,
    Liner,
    LeanFly,
    FlyBall,
}

public static class LaunchTendencyTypes
{
    /// <summary>
    /// LaunchTendency(1〜100)からタイプを求める。角度への変換（launch_angle_at_lt1/lt100, coefficients.yaml）が
    /// 単調線形なので、LT値を等分割すれば角度も等分割になる（issue #219 コメントで採用）。
    /// </summary>
    public static LaunchTendencyType FromValue(int launchTendency) => launchTendency switch
    {
        <= 20 => LaunchTendencyType.GroundBall,
        <= 40 => LaunchTendencyType.LeanGround,
        <= 60 => LaunchTendencyType.Liner,
        <= 80 => LaunchTendencyType.LeanFly,
        _ => LaunchTendencyType.FlyBall,
    };
}

using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 弾道タイプ（LaunchTendencyType）の日本語表示名の単一ソース（issue #219）。
    /// 内部の LaunchTendency(1〜100) はそのまま、表示だけタイプラベルに差し替える画面向け。
    /// </summary>
    public static class LaunchTendencyLabels
    {
        public static string Jp(LaunchTendencyType t) => t switch
        {
            LaunchTendencyType.GroundBall => "ゴロ型",
            LaunchTendencyType.LeanGround => "ややゴロ型",
            LaunchTendencyType.Liner => "ライナー型",
            LaunchTendencyType.LeanFly => "ややフライ型",
            LaunchTendencyType.FlyBall => "フライ型",
            _ => t.ToString(),
        };

        public static string Jp(int launchTendency) => Jp(LaunchTendencyTypes.FromValue(launchTendency));
    }
}

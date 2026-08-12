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

        /// <summary>
        /// 一覧の狭い列に収める段表示（1＝ゴロ型 … 5＝フライ型。issue #602）。
        /// 「ややフライ型」は列に入らないので、野球ゲーム慣行の段番号で出す。
        /// <b>優劣ではなく型の段</b>なので、ランクチップにも色にもしないこと（issue #219）。
        /// </summary>
        public static int Step(int launchTendency) => (int)LaunchTendencyTypes.FromValue(launchTendency) + 1;
    }
}

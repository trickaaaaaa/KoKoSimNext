using KokoSim.Engine.Nation;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// <see cref="TeamStrengthCoefficients"/>（既定値）の単一静的ソース（Issue #140）。
    /// チーム総合力・選手カテゴリ別ランクを計算する全画面（PlayerList/PlayerDetail/TeamOverall/
    /// TeamStrength/PracticeMatch/MatchPreview）がそれぞれ同値を <c>new</c> していた重複を集約する。
    /// </summary>
    public static class TeamStrengthCoeff
    {
        public static readonly TeamStrengthCoefficients Default = new TeamStrengthCoefficients();
    }
}

using KokoSim.Engine.Match.Fielding;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// <see cref="FieldingCoefficients"/>（既定値）の単一静的ソース（issue #371, TeamStrengthCoeff と同型）。
    /// 実効守備力ランク表示（<see cref="KokoSim.Engine.Season.EffectiveFielding"/> 経由）を出す全画面が
    /// それぞれ同値を <c>new</c> するのを避ける。
    /// </summary>
    public static class FieldingCoeff
    {
        public static readonly FieldingCoefficients Default = new FieldingCoefficients();
    }
}

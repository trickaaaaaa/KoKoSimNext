#if UNITY_EDITOR
using KokoSim.Engine.Nation;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// 撮影レシピ用：自校のスクールカラーを1色に差し替える（帽子・アンダーシャツ・ストッキングを同じ色に）。
    /// <c>call: SchoolColorProbe.Set("#F2C200")</c>。白・黄色・水色の学校で表示用の色（SchoolAccent.ToDisplay）を見る用。
    /// </summary>
    public static class SchoolColorProbe
    {
        public static void Set(string hex)
        {
            if (!RgbColor.TryParseHex(hex, out var c)) return;
            var white = new RgbColor(0xF4, 0xF2, 0xEC);
            TeamColorProvider.ManagerOverride = new TeamColors(
                Shirt: white, Pants: white, Undershirt: c, Socks: c,
                CapCrown: c, CapVisor: c, Shoes: TeamColors.DefaultShoes, Stripe: TeamStripe.None);
        }
    }
}
#endif

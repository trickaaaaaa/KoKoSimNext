using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ランク色（S〜G）のコード側単一ソース。UIの色は tokens.uss --rank-* が正で、
    /// これはコードから色が要る箇所（レーダー・バー塗り等）向けのミラー。値は必ず一致させること
    /// （tokens.uss / KokoSimTheme.uss .grade--X / このクラスの3面を同期）。
    /// S=金 / A=赤 / B=橙 / C=黄 / D=黄緑 / E=緑 / F=グレー / G=暗灰。
    /// </summary>
    public static class RankPalette
    {
        public static string Hex(string grade)
        {
            switch (grade)
            {
                case "S": return "#F4CE5B";
                case "A": return "#E4553B";
                case "B": return "#EC8B3C";
                case "C": return "#E6C245";
                case "D": return "#A8C64E";
                case "E": return "#56A96E";
                case "F": return "#7E8C93";
                default:  return "#566257"; // G
            }
        }

        public static Color Of(string grade)
            => ColorUtility.TryParseHtmlString(Hex(grade), out var c) ? c : Color.gray;
    }
}

using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ランク色（S〜G）のコード側単一ソース。UIの色は tokens.uss --rank-* が正で、
    /// これはコードから色が要る箇所（レーダー・バー塗り等）向けのミラー。値は必ず一致させること
    /// （tokens.uss とこのクラスの2面を同期。チップ自体は画像 Assets/UI/Images/ranks/ 表示）。
    /// パワプロ風レター画像から抽出した代表色: S=金 / A=ピンク / B=赤 / C=黄 / D=緑 / E=青 / F=銀 / G=灰。
    /// </summary>
    public static class RankPalette
    {
        public static string Hex(string grade)
        {
            switch (grade)
            {
                case "S": return "#E19900";
                case "A": return "#F3279B";
                case "B": return "#EF2323";
                case "C": return "#F4CB3C";
                case "D": return "#56C662";
                case "E": return "#409AF6";
                case "F": return "#B1B9C1";
                default:  return "#81888E"; // G
            }
        }

        public static Color Of(string grade)
            => ColorUtility.TryParseHtmlString(Hex(grade), out var c) ? c : Color.gray;
    }
}

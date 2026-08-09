// 球種の表示語彙の単一ソース。正式名（Jp）と略記（Abbr）の2つを持つ。
// 以前は選手詳細と試合ライブに同じ switch が二重に置かれていた（issue #560 でここへ寄せた）。
using KokoSim.Engine.Players;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 球種の日本語表示（設計書02 §2.1 の9球種）。表示語彙なので engine には置かず Shell 側に集約する。
    /// </summary>
    public static class PitchTypeLabels
    {
        /// <summary>正式名（「チェンジアップ」）。文字数に余裕のある所はこちらを使う。</summary>
        public static string Jp(PitchType t)
        {
            switch (t)
            {
                case PitchType.Fastball: return "ストレート";
                case PitchType.TwoSeam: return "ツーシーム";
                case PitchType.Cutter: return "カットボール";
                case PitchType.Slider: return "スライダー";
                case PitchType.Curve: return "カーブ";
                case PitchType.Fork: return "フォーク";
                case PitchType.Changeup: return "チェンジアップ";
                case PitchType.Shuuto: return "シュート";
                case PitchType.Sinker: return "シンカー";
                default: return t.ToString();
            }
        }

        /// <summary>
        /// 略記（最大3文字）。狭い器で球種名を出すためのもので、いまは比較カードの小型球種チャート
        /// （<c>PitchChartView(compact: true)</c>）だけが使う（issue #560）。
        /// <para>正式名のままだと「チェンジアップ」1枚で約108px＝器（約170px）の6割を占め、
        /// 4球種のラベルが幾何的に収まらない。3文字なら約60pxで、横に2枚・縦に3段まで置ける。</para>
        /// <para>頭の1〜2文字だけでは「カット」と「カーブ」、「シュート」と「シンカー」が
        /// 見分けづらいため、衝突する組だけ文字数を増やしてある。</para>
        /// </summary>
        public static string Abbr(PitchType t)
        {
            switch (t)
            {
                case PitchType.Fastball: return "直";      // 直球
                case PitchType.TwoSeam: return "ツー";
                case PitchType.Cutter: return "カット";
                case PitchType.Slider: return "スラ";
                case PitchType.Curve: return "カーブ";     // 「カ」だと カット と紛れる
                case PitchType.Fork: return "フォ";
                case PitchType.Changeup: return "チェ";
                case PitchType.Shuuto: return "シュ";
                case PitchType.Sinker: return "シン";
                default: return Jp(t);
            }
        }
    }
}

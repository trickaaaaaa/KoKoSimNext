using KokoSim.Engine.Match.Game;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// ScoreBug（部品辞書 / 試合画面改修）の充填ロジック。
    /// 見た目は Components/components.uss の .sbug*、器は Components/ScoreBug.uxml。
    ///
    /// 打席の演出中も出しっぱなしにする常時表示なので、<see cref="LineScorePanel"/> と違って
    /// 「確定した瞬間に1回だけ掲げる」運用はしない。数値はすべてエンジンの観測データ
    /// （<see cref="LiveLineScore"/>）から引き、UI側で得点を組み立てない。
    ///
    /// カウント（B/S/O）と塁は1球ごとに動くので、画面コントローラが
    /// <c>sbug-balls</c> / <c>sbug-base-1</c> 等の name を直接駆動する（この部品は器と見た目だけを持つ）。
    /// </summary>
    public static class ScoreBug
    {
        /// <summary>
        /// 回表示・校名・得点・攻撃中の印を埋める。inning/isTop は現在の回（表裏）。
        /// finished=true なら回の代わりに「試合終了」を出し、攻撃中の印を消す。
        /// </summary>
        public static void Fill(
            VisualElement root, LiveLineScore away, LiveLineScore home,
            int inning, bool isTop, bool finished)
        {
            if (root == null || away == null || home == null) return;
            if (root.Q("sbug-root") == null) return;

            SetText(root, "sbug-away-name", away.Name);
            SetText(root, "sbug-home-name", home.Name);
            SetText(root, "sbug-away-runs", away.Runs.ToString());
            SetText(root, "sbug-home-runs", home.Runs.ToString());

            SetText(root, "sbug-inning-num", finished ? "" : inning.ToString());
            SetText(root, "sbug-inning-half", finished ? "試合終了" : (isTop ? "回表" : "回裏"));

            // 攻撃中の校に縦バー。終局後はどちらにも付けない。
            root.Q("sbug-away")?.EnableInClassList("sbug-team--batting", !finished && isTop);
            root.Q("sbug-home")?.EnableInClassList("sbug-team--batting", !finished && !isTop);
        }

        private static void SetText(VisualElement root, string name, string text)
        {
            var label = root.Q<Label>(name);
            if (label != null) label.text = text;
        }
    }
}

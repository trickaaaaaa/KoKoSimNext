using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 能力の3点セット（スコアブック試作スキン・scorebook.uss の .sbk-bar）：
    /// ランクチップ（22px）＋数値（28px・右揃え）＋横棒（残りの幅・左から右へ伸びる）。
    /// 比較表の片側1人ぶん。どちらの側かで棒の色が変わる（自チーム側＝差し色／比較相手＝墨）。
    /// 勝ち＝数値を太字、負け＝数値を淡色・棒を 0.35、同値＝どちらも通常。
    /// </summary>
    public sealed class StatBarRow : VisualElement
    {
        public enum Side { Own, Other }
        public enum Outcome { Even, Win, Lose }

        /// <param name="grade">ランク（S〜G）。空ならチップを出さない。</param>
        /// <param name="value">表示する数値。</param>
        /// <param name="fillPercent">棒の長さ（0〜100）。</param>
        public StatBarRow(string grade, int value, float fillPercent, Side side, Outcome outcome)
        {
            AddToClassList("sbk-bar");
            AddToClassList(side == Side.Own ? "sbk-bar--a" : "sbk-bar--b");
            if (outcome == Outcome.Lose) AddToClassList("sbk-bar--lose");

            if (!string.IsNullOrEmpty(grade))
            {
                var chip = UiComponents.RankChip(grade);
                chip.AddToClassList("rank-chip--sbk22");
                Add(chip);
            }

            var val = new Label(value.ToString());
            val.AddToClassList("sbk-bar__val");
            val.AddToClassList(outcome == Outcome.Win ? "f-num-bd" : "f-num");
            Add(val);

            var track = new VisualElement();
            track.AddToClassList("sbk-bar__track");
            var fill = new VisualElement();
            fill.AddToClassList("sbk-bar__fill");
            if (side == Side.Own) fill.AddToClassList("acc-bg");
            fill.style.width = Length.Percent(Mathf.Clamp(fillPercent, 0f, 100f));
            track.Add(fill);
            Add(track);
        }

        /// <summary>数値の無い行（弾道のタイプ表示など）。テキストだけを置く。</summary>
        public static VisualElement TextOnly(string text)
        {
            var el = new VisualElement();
            el.AddToClassList("sbk-bar");
            var l = new Label(text ?? "");
            l.AddToClassList("sbk-bar__text");
            el.Add(l);
            return el;
        }

        /// <summary>選手がいない側の空セル。</summary>
        public static VisualElement Empty()
        {
            var el = new VisualElement();
            el.AddToClassList("sbk-bar");
            return el;
        }

        /// <summary>勝敗（-1=自チーム側の勝ち, 1=比較相手の勝ち, 0=同値）を片側から見た結果に直す。</summary>
        public static Outcome OutcomeFor(Side side, int winner)
        {
            if (winner == 0) return Outcome.Even;
            var ownWins = winner < 0;
            return (side == Side.Own) == ownWins ? Outcome.Win : Outcome.Lose;
        }
    }
}

using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// ランクの札（試合開始前の画面の総合ランク）。<see cref="UiComponents.RankChip"/> がレター画像なのに対し、
    /// こちらは文字（Dela Gothic One＝<c>.f-rank</c>・ランクの文字にだけ使う書体）をランクの色の地に載せる。
    /// 大きさ・縁取りは使う画面が <c>.rank-badge</c> に足して決める（色は components.uss の <c>.rank-badge--*</c>）。
    /// </summary>
    public sealed class RankBadge : Label
    {
        private static readonly string[] Grades = { "S", "A", "B", "C", "D", "E", "F", "G" };

        public RankBadge(string grade = "")
        {
            AddToClassList("rank-badge");
            AddToClassList("f-rank");
            pickingMode = PickingMode.Ignore;
            Set(grade);
        }

        public void Set(string grade)
        {
            text = grade ?? "";
            foreach (var g in Grades) RemoveFromClassList("rank-badge--" + g);
            if (!string.IsNullOrEmpty(grade)) AddToClassList("rank-badge--" + grade);
        }
    }
}

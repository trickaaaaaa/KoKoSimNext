using KokoSim.Engine.Nation;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 共通トップバー右側メーター（部費残高・名声・信頼度）の一括埋め（issue #172 追補）。
    /// 値は <see cref="ManagerService.Manager"/> が単一ソースで、名声・信頼度は全画面共通の
    /// <see cref="Tiers.FromStrength"/>（S〜G）で等級化し、選手能力と同じレター画像チップ
    /// （KokoSimTheme.uss .grade--X）で表示する。各画面のコントローラが ScoreboardStrip.Fill と並べて呼ぶ。
    /// </summary>
    public static class TopBarMeters
    {
        private static readonly string[] Grades = { "S", "A", "B", "C", "D", "E", "F", "G" };

        public static void Fill(VisualElement root)
        {
            if (root == null) return;
            var m = ManagerService.Manager;
            Set(root, "funds", "¥" + m.Funds.ToString("0") + "万");
            SetRank(root, "fame", Tiers.FromStrength(m.Fame).ToString());
            SetRank(root, "trust", Tiers.FromStrength(m.Trust).ToString());
        }

        private static void Set(VisualElement root, string name, string text)
        {
            var label = root.Q<Label>(name);
            if (label != null) label.text = text;
        }

        /// <summary>名声・信頼度をレター画像チップにする（文字はレイアウト用に残し透明化）。</summary>
        private static void SetRank(VisualElement root, string name, string grade)
        {
            var label = root.Q<Label>(name);
            if (label == null) return;
            label.text = grade;
            foreach (var g in Grades) label.RemoveFromClassList("grade--" + g);
            label.AddToClassList("grade");
            label.AddToClassList("grade--" + grade);
            // .sb2-meter__v の文字色指定が .grade の透明化に勝つことがあるため、インラインで確実に消す。
            label.style.color = Color.clear;
        }
    }
}

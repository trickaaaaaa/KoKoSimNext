using KokoSim.Engine.Nation;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 共通トップバーの動的値（自校の表札＋部費残高・名声・信頼度）の一括埋め（issue #172 追補 / #585）。
    /// 値は <see cref="ManagerService.Manager"/> と <see cref="NationService"/> が単一ソースで、
    /// 名声・信頼度は全画面共通の <see cref="Tiers.FromStrength"/>（S〜G）で等級化し、
    /// 選手能力と同じレター画像チップ（KokoSimTheme.uss .grade--X）で表示する。
    /// 各画面のコントローラが ScoreboardStrip.Fill と並べて呼ぶ。
    ///
    /// <para><b>自校の表札（校章・校名・県名）もここが埋める</b>（issue #585）。校名・本拠県が
    /// プレイヤーの選択で変わるようになったため、UXML の既定値（「県立桜丘高校」）のままだと
    /// ホーム以外の全画面が別の学校の名前を出す＝実際にそうなっていた。</para>
    /// </summary>
    public static class TopBarMeters
    {
        private static readonly string[] Grades = { "S", "A", "B", "C", "D", "E", "F", "G" };

        public static void Fill(VisualElement root)
        {
            if (root == null) return;
            Set(root, "badge", BadgeOf(NationService.ManagerSchoolName));
            Set(root, "school-name", SchoolDisplayName());
            Set(root, "pref", PrefectureNames.JapaneseOf(NationService.PrefectureId));

            var m = ManagerService.Manager;
            Set(root, "funds", "¥" + m.Funds.ToString("0") + "万");
            SetRank(root, "fame", Tiers.FromStrength(m.Fame).ToString());
            SetRank(root, "trust", Tiers.FromStrength(m.Trust).ToString());
        }

        /// <summary>表札に出す自校名（校名＋「高校」）。校名の幹だけを保持し、接尾は表示側で付ける。
        /// 「県立」は付けない＝校名はプレイヤーが自由に決めるもので、設置者は決めていない。</summary>
        public static string SchoolDisplayName() => NationService.ManagerSchoolName + "高校";

        /// <summary>校章代わりの1文字（校名の先頭字）。空なら「校」。</summary>
        private static string BadgeOf(string name)
            => string.IsNullOrEmpty(name) ? "校" : name.Substring(0, 1);

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

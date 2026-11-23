using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 特殊能力の変化（後天習得・発現・気づきの開示・克服＝issue #1471・設計書10 §3）の文言の単一ソース。
    /// 通知フィードの1行・ノベルのイベントID・会話の結果行をここだけで決める（<see cref="GrowthEventText"/> と同じ作法）。
    /// スキル名は data/skills.yaml の表示名（<see cref="SkillCatalogProvider.Name"/>）。
    /// </summary>
    public static class SkillChangeText
    {
        /// <summary>
        /// data/events.yaml のイベントID。きっかけごとに会話を分ける（覚醒・実戦・学年・気づき）。
        /// 克服はきっかけ（実戦／学年）で語り口が違うので別ID。
        /// </summary>
        public static string EventId(SkillChangeNotice n)
        {
            if (n.Kind == SkillChangeKind.Overcome)
                return n.Cause == SkillChangeCause.Match ? "skill_overcome_match" : "skill_overcome_grade";
            switch (n.Cause)
            {
                case SkillChangeCause.Insight: return "skill_insight_revealed";
                case SkillChangeCause.Awakening: return "skill_awakening";
                default: return "skill_match_acquired";
            }
        }

        /// <summary>通知フィードのタグ（成長 / 発見）。気づきの開示は発見、ほかは成長。</summary>
        public static string Tag(SkillChangeNotice n) => n.Kind == SkillChangeKind.Revealed ? "発見" : "成長";

        /// <summary>通知フィードに残す1行。</summary>
        public static string Feed(SkillChangeNotice n)
        {
            var who = n.Player.Name;
            var skill = "「" + SkillCatalogProvider.Name(n.Skill) + "」";
            switch (n.Kind)
            {
                case SkillChangeKind.Revealed: return who + " の" + skill + "に気づいた";
                case SkillChangeKind.Manifested: return who + " の眠っていた" + skill + "が目を覚ました";
                case SkillChangeKind.Overcome: return who + " が" + skill + "を克服した";
                default: return who + " が" + skill + "を身につけた";
            }
        }

        /// <summary>会話の最終行に出す結果行（誰の・何が変わったかを1文で言い切る）。</summary>
        public static PresentedEffect Effect(SkillChangeNotice n)
        {
            var who = n.Player.Name;
            var skill = "「" + SkillCatalogProvider.Name(n.Skill) + "」";
            switch (n.Kind)
            {
                case SkillChangeKind.Revealed: return PresentedEffect.Good(who + " の特殊能力" + skill + "が分かった！");
                case SkillChangeKind.Overcome: return PresentedEffect.Good(who + " の" + skill + "が消えた！");
                default: return PresentedEffect.Good(who + " が特殊能力" + skill + "を身につけた！");
            }
        }
    }
}

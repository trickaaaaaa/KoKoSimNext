using System.Collections.Generic;
using KokoSim.Engine.Career;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 監督成長の節目（設計書04 §1.1b・issue #612）の文言の単一ソース。
    /// 年に複数の節目が起きても<b>語るのは1件</b>にする（クリックを増やさない）。
    /// 効果帯には実測の差分を全部並べるので、選ばれなかった節目のぶんも数字には出る。
    /// </summary>
    public static class ManagerGrowthText
    {
        /// <summary>
        /// その年の会話に使う1件を選ぶ。<b>珍しい順・大きい順</b>＝甲子園 &gt; 名伯楽 &gt; プロOB &gt;
        /// 名将 &gt; 大敗 &gt; 講習会。同率が無いので決定論（乱数を引かない）。
        /// </summary>
        public static ManagerGrowthEventType Lead(IReadOnlyList<ManagerGrowthNotice> notices)
        {
            var best = notices[0].Type;
            foreach (var n in notices) if (Rank(n.Type) < Rank(best)) best = n.Type;
            return best;
        }

        private static int Rank(ManagerGrowthEventType t)
        {
            switch (t)
            {
                case ManagerGrowthEventType.KoshienExperience: return 0;
                case ManagerGrowthEventType.MentorCoach: return 1;
                case ManagerGrowthEventType.ProObVisit: return 2;
                case ManagerGrowthEventType.FamousRivalLesson: return 3;
                case ManagerGrowthEventType.LessonFromBigLoss: return 4;
                default: return 5;
            }
        }

        /// <summary>data/events.yaml のイベントID。</summary>
        public static string EventId(ManagerGrowthEventType t)
        {
            switch (t)
            {
                case ManagerGrowthEventType.KoshienExperience: return "mgr_koshien_experience";
                case ManagerGrowthEventType.MentorCoach: return "mgr_mentor";
                case ManagerGrowthEventType.ProObVisit: return "mgr_pro_ob";
                case ManagerGrowthEventType.FamousRivalLesson: return "mgr_rival_lesson";
                case ManagerGrowthEventType.LessonFromBigLoss: return "mgr_big_loss";
                default: return "mgr_seminar";
            }
        }

        /// <summary>効果帯の見出し（何があった年か）。</summary>
        public static string Title(ManagerGrowthEventType t)
        {
            switch (t)
            {
                case ManagerGrowthEventType.KoshienExperience: return "甲子園の経験";
                case ManagerGrowthEventType.MentorCoach: return "名伯楽との出会い";
                case ManagerGrowthEventType.ProObVisit: return "プロOBの訪問";
                case ManagerGrowthEventType.FamousRivalLesson: return "名将との一戦";
                case ManagerGrowthEventType.LessonFromBigLoss: return "大敗からの学び";
                default: return "指導者講習会";
            }
        }

        /// <summary>通知フィードの1行。動いた軸を並べる（動かなかった年は見出しだけ）。</summary>
        public static string Feed(ManagerGrowthEventType lead, ManagerGrowthDelta delta)
        {
            var items = delta.ToItems();
            if (items.Count == 0) return Title(lead) + "。";

            var sb = new System.Text.StringBuilder(Title(lead));
            sb.Append("（");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append("・");
                sb.Append(items[i].Label).Append(' ').Append(items[i].Value).Append(items[i].Unit);
            }
            sb.Append("）");
            return sb.ToString();
        }
    }
}

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
        /// その年の会話に使う1件を選ぶ。<b>珍しい順・大きい順</b>＝甲子園 &gt; 名伯楽 &gt; 教え子の訪問 &gt;
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

        /// <summary>指導力の分野の呼び名（「打撃」「投手」「守備走塁」。会話の {area}・結果行の「○○指導」）。</summary>
        public static string AreaLabel(CoachingArea a)
        {
            switch (a)
            {
                case CoachingArea.Batting: return "打撃";
                case CoachingArea.Pitching: return "投手";
                default: return "守備走塁";
            }
        }

        /// <summary>その分野の指導力の値（結果行を実際に動いた量で書くために前後を測る）。</summary>
        public static double CoachingOf(Manager m, CoachingArea a)
        {
            switch (a)
            {
                case CoachingArea.Batting: return m.CoachingBatting;
                case CoachingArea.Pitching: return m.CoachingPitching;
                default: return m.CoachingDefense;
            }
        }

        /// <summary>
        /// 講習会・大敗の振り返りで「その分野の何を学んだか」の1文（会話の {lesson}）。
        /// 具体的な中身を言い切ることで、指導力がなぜ上がったかが分かる（issue #1483）。
        /// </summary>
        public static string Lesson(CoachingArea a)
        {
            switch (a)
            {
                case CoachingArea.Batting: return "ボールを呼び込む間と、振り出しの順番";
                case CoachingArea.Pitching: return "下半身の使い方と、球数の割り振り";
                default: return "中継の声かけと、先の塁を読む目";
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
                default: return "coaching_seminar";   // 講習会は実ゲームでは冬に本人へ聞く（年度末の総括には載らない）
            }
        }

        /// <summary>節目の見出し（何があった年か。通知フィードの頭）。</summary>
        public static string Title(ManagerGrowthEventType t)
        {
            switch (t)
            {
                case ManagerGrowthEventType.KoshienExperience: return KokoSim.Engine.Season.DisplayNames.Current.Koshien + "の経験";
                case ManagerGrowthEventType.MentorCoach: return "名伯楽との出会い";
                case ManagerGrowthEventType.ProObVisit: return "教え子の訪問";
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

        /// <summary>
        /// 会話の最終行に出す結果行（監督指示 2026-08-19）。「采配が ＋3、育成眼が ＋3 上がった！」＝
        /// 動いた軸を全部並べる（<see cref="Feed"/> と同じ材料）。何も動かなかった年は null（結果行なし）。
        /// 上がった軸と下がった軸（大敗の年の名声など）は文を分ける＝「＋3、−12 動いた」のように混ぜない。
        /// 部費だけ単位が違うので「部費 ＋10 万」の形で最後に置く。
        /// </summary>
        public static PresentedEffect Effect(ManagerGrowthDelta delta)
        {
            var items = delta.ToItems();
            if (items.Count == 0) return null;

            var ups = new List<string>();
            var downs = new List<string>();
            string funds = null;
            foreach (var it in items)
            {
                if (!string.IsNullOrEmpty(it.Unit)) { funds = it.Label + " " + it.Value + " " + it.Unit; continue; }
                (it.Value.StartsWith("−") ? downs : ups).Add(it.Label + "が " + it.Value);
            }

            var parts = new List<string>();
            if (ups.Count > 0) parts.Add(string.Join("、", ups.ToArray()) + " 上がった！");
            if (downs.Count > 0) parts.Add(string.Join("、", downs.ToArray()) + " 下がった");
            if (funds != null) parts.Add(funds);
            var sentence = string.Join("　", parts.ToArray());
            // 監督の節目は伸びる話なので、1軸でも上がっていれば吉報の色。下がるだけの年（大敗）は警告色。
            return ups.Count > 0 ? PresentedEffect.Good(sentence) : PresentedEffect.Bad(sentence);
        }
    }
}

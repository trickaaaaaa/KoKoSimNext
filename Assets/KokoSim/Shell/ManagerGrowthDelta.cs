using System.Collections.Generic;
using KokoSim.Engine.Career;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 1年ぶんの監督メタの動き（issue #612）。<b>適用の前後を測って作る</b>＝
    /// 節目イベントの種別から効果を推測しない（engine は「弱点分野が伸びる」「名声も動く」など
    /// 種別だけでは決まらない分岐を持つ。推測すると帯の数字だけが嘘になる）。
    /// </summary>
    public sealed class ManagerGrowthDelta
    {
        public double Batting, Pitching, Defense, Tactical, TalentEye, Fame, Trust, Funds;

        /// <summary>適用前の値を控える。</summary>
        public static ManagerGrowthDelta Snapshot(Manager m) => new ManagerGrowthDelta
        {
            Batting = m.CoachingBatting,
            Pitching = m.CoachingPitching,
            Defense = m.CoachingDefense,
            Tactical = m.TacticalSense,
            TalentEye = m.TalentEye,
            Fame = m.Fame,
            Trust = m.Trust,
            Funds = m.Funds,
        };

        /// <summary>控えた値と現在値の差。</summary>
        public static ManagerGrowthDelta Since(ManagerGrowthDelta before, Manager now) => new ManagerGrowthDelta
        {
            Batting = now.CoachingBatting - before.Batting,
            Pitching = now.CoachingPitching - before.Pitching,
            Defense = now.CoachingDefense - before.Defense,
            Tactical = now.TacticalSense - before.Tactical,
            TalentEye = now.TalentEye - before.TalentEye,
            Fame = now.Fame - before.Fame,
            Trust = now.Trust - before.Trust,
            Funds = now.Funds - before.Funds,
        };

        /// <summary>
        /// 効果帯の項目へ変換する。<b>動いた軸だけ</b>並べる（0 の軸を「＋0」で並べると
        /// 何が起きたのか読めなくなる）。部費は別の単位（万円）なので最後に置く。
        /// </summary>
        public IReadOnlyList<EffectItem> ToItems()
        {
            var items = new List<EffectItem>();
            Add(items, "打撃指導", Batting);
            Add(items, "投手指導", Pitching);
            Add(items, "守備指導", Defense);
            Add(items, "采配", Tactical);
            Add(items, "育成眼", TalentEye);
            Add(items, "名声", Fame);
            Add(items, "信頼度", Trust);
            if (System.Math.Abs(Funds) >= 0.5)
                items.Add(new EffectItem("部費", Signed(Funds), "万"));
            return items;
        }

        /// <summary>1つも動かなかった年（効果帯を出さない判断に使う）。</summary>
        public bool IsEmpty => ToItems().Count == 0;

        private static void Add(List<EffectItem> items, string label, double value)
        {
            if (System.Math.Abs(value) < 0.5) return;   // 表示は整数なので、丸めて0になる動きは出さない
            items.Add(new EffectItem(label, Signed(value)));
        }

        // 全角の符号で揃える（＋/− を半角にすると Oswald と混植になって高さが揃わない）。
        private static string Signed(double value)
        {
            var n = System.Math.Round(System.Math.Abs(value));
            return (value >= 0 ? "＋" : "−") + n.ToString("0");
        }
    }
}

using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 選手成長イベント（設計書03 §5.5・issue #611）の文言と効果帯の単一ソース。
    /// 通知フィードの1行・ノベルのイベントID・効果帯の中身をここだけで決める
    /// （3箇所に散ると「フィードには出るのに会話が出ない」形でずれる）。
    ///
    /// <para><b>数値は係数と実測から作る。</b>係数は engine の適用に使ったのと<b>同じインスタンス</b>を
    /// 受け取るので食い違わない。スランプ週数だけは適用後の
    /// <see cref="DevelopingPlayer.SlumpWeeks"/> をそのまま読む（＝抽選された実際の週数）。</para>
    /// </summary>
    public static class GrowthEventText
    {
        /// <summary>吉報か（通知フィードの色分けとタグに使う）。</summary>
        public static bool IsGood(GrowthEventType t)
        {
            return t == GrowthEventType.Awakening
                   || t == GrowthEventType.Breakthrough
                   || t == GrowthEventType.YipsOvercome;
        }

        /// <summary>data/events.yaml のイベントID（会話の引き当て）。</summary>
        public static string EventId(GrowthEventType t)
        {
            switch (t)
            {
                case GrowthEventType.Awakening: return "growth_awakening";
                case GrowthEventType.Breakthrough: return "growth_breakthrough";
                case GrowthEventType.Slump: return "growth_slump";
                case GrowthEventType.Plateau: return "growth_plateau";
                case GrowthEventType.Yips: return "growth_yips";
                default: return "growth_yips_overcome";
            }
        }

        /// <summary>通知フィードに残す1行（ノベルを見逃しても何が起きたか追える）。</summary>
        public static string Feed(GrowthNotice n, GrowthEventCoefficients c)
        {
            var who = n.Player.Name;
            var ability = n.Ability.HasValue ? AbilityLabels.Jp(n.Ability.Value) : "";
            switch (n.Type)
            {
                case GrowthEventType.Awakening:
                    return who + " が覚醒した（" + ability + " ＋" + c.AwakeningGain + "）";
                case GrowthEventType.Breakthrough:
                    return who + " がコツを掴んだ（" + ability + " ＋" + c.BreakthroughGain + "）";
                case GrowthEventType.Slump:
                    return who + " がスランプに入った（" + n.Player.SlumpWeeks + " 週）";
                case GrowthEventType.Plateau:
                    return who + " の " + ability + " が頭打ちになった";
                case GrowthEventType.Yips:
                    return who + " が " + ability + " を崩した（−" + c.YipsAbilityDrop + "）";
                default:
                    return who + " が " + ability + " の不調を克服した";
            }
        }

        /// <summary>
        /// 会話の最終行に出す結果行（監督指示 2026-08-19。旧・案A2 の帯は廃止）。
        /// 「{player} のミートが ＋5 上がった！」＝誰の・何が・いくつを1文で言い切る。
        /// 数値は <see cref="Feed"/> と同じ係数・同じ実測から作る＝フィードと会話で食い違わない。
        /// </summary>
        public static PresentedEffect Effect(GrowthNotice n, GrowthEventCoefficients c)
        {
            var who = n.Player.Name;
            var ability = n.Ability.HasValue ? AbilityLabels.Jp(n.Ability.Value) : "能力";
            switch (n.Type)
            {
                case GrowthEventType.Awakening:
                    return PresentedEffect.Good(who + " の" + ability + "が ＋" + c.AwakeningGain + " 上がった！");
                case GrowthEventType.Breakthrough:
                    return PresentedEffect.Good(who + " の" + ability + "が ＋" + c.BreakthroughGain + " 上がった！");
                case GrowthEventType.Slump:
                    // 週数は抽選済みの実測値。能力の落ち幅は倍率なので「−7%」の形で出す。
                    return PresentedEffect.Bad(who + " がスランプに入った（能力 −" + Percent(c.SlumpPerformanceFactor)
                                               + "%・" + n.Player.SlumpWeeks + " 週）");
                case GrowthEventType.Plateau:
                    // 恒久の頭打ち。数値を出すと「劣化した」に見えるので、伸びしろの話として書く。
                    return PresentedEffect.Bad(who + " の" + ability + "は、もう大きくは伸びない");
                case GrowthEventType.Yips:
                    return PresentedEffect.Bad(who + " の" + ability + "が −" + c.YipsAbilityDrop + " 下がった");
                default:
                    return PresentedEffect.Good(who + " の" + ability + "が ＋" + c.YipsAbilityDrop + " 戻った！");
            }
        }

        // 0.93 → "7"（落ち幅の％）。四捨五入で整数にする＝小数の％は帯で読みにくい。
        private static string Percent(double factor)
        {
            return System.Math.Round((1.0 - factor) * 100.0).ToString("0");
        }
    }
}

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
            switch (t)
            {
                case GrowthEventType.Slump:
                case GrowthEventType.Plateau:
                case GrowthEventType.Yips:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// data/events.yaml のイベントID（会話の引き当て）。覚醒と大舞台は分野で会話が違う（issue #694。
        /// 投手が覚醒して「スイングが別人に」と語られていた）。
        /// </summary>
        public static string EventId(GrowthNotice n)
        {
            switch (n.Type)
            {
                case GrowthEventType.Awakening: return "growth_awakening_" + DomainKey(n.Domain);
                case GrowthEventType.GrandAwakening: return "growth_grand_awakening";
                case GrowthEventType.GrowthSurge: return "growth_surge";
                case GrowthEventType.NewPitch: return "growth_new_pitch";
                case GrowthEventType.PitchRefined: return "growth_pitch_refined";
                case GrowthEventType.BigStage:
                    return n.Domain == GrowthDomain.Pitching ? "growth_big_stage_pitching" : "growth_big_stage_batting";
                case GrowthEventType.Breakthrough: return "growth_breakthrough";
                case GrowthEventType.Slump: return "growth_slump";
                case GrowthEventType.Plateau: return "growth_plateau";
                case GrowthEventType.Yips: return "growth_yips";
                default: return "growth_yips_overcome";
            }
        }

        private static string DomainKey(GrowthDomain? d)
        {
            switch (d)
            {
                case GrowthDomain.Pitching: return "pitching";
                case GrowthDomain.Defense: return "defense";
                case GrowthDomain.Running: return "running";
                default: return "batting";
            }
        }

        /// <summary>分野の呼び名（伸びしろアップの文に使う）。</summary>
        private static string DomainJp(GrowthDomain? d)
        {
            switch (d)
            {
                case GrowthDomain.Pitching: return "投球";
                case GrowthDomain.Defense: return "守備";
                case GrowthDomain.Running: return "走塁";
                default: return "打撃";
            }
        }

        /// <summary>
        /// 実際に上がった能力を「ミート ＋4・パワー ＋4」の形にする（issue #694。上限で止まった分は載らない）。
        /// 精神力の上昇（大舞台）も同じ列に並べる。
        /// </summary>
        private static string GainsText(GrowthNotice n)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var g in n.Gains) parts.Add(AbilityLabels.Jp(g.Ability) + " ＋" + g.Amount);
            if (n.MentalGain > 0) parts.Add("精神力 ＋" + n.MentalGain);
            return string.Join("・", parts);
        }

        /// <summary>結果行の形「パワーが ＋4、ミートが ＋4」（<see cref="GainsText"/> と同じ材料）。</summary>
        private static string GainsSentence(GrowthNotice n)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var g in n.Gains) parts.Add(AbilityLabels.Jp(g.Ability) + "が ＋" + g.Amount);
            if (n.MentalGain > 0) parts.Add("精神力が ＋" + n.MentalGain);
            return string.Join("、", parts);
        }

        private static string PitchJp(GrowthNotice n) => n.Pitch.HasValue ? PitchTypeLabels.Jp(n.Pitch.Value) : "変化球";

        /// <summary>通知フィードに残す1行（ノベルを見逃しても何が起きたか追える）。</summary>
        public static string Feed(GrowthNotice n, GrowthEventCoefficients c)
        {
            var who = n.Player.Name;
            var ability = n.Ability.HasValue ? AbilityLabels.Jp(n.Ability.Value) : "";
            switch (n.Type)
            {
                case GrowthEventType.Awakening:
                    return who + " が覚醒した（" + GainsText(n) + "）";
                case GrowthEventType.GrandAwakening:
                    return who + " が大覚醒した（" + GainsText(n) + "）";
                case GrowthEventType.GrowthSurge:
                    return who + " の" + DomainJp(n.Domain) + "に伸びしろが見えた";
                case GrowthEventType.NewPitch:
                    return who + " が" + PitchJp(n) + "を覚えた";
                case GrowthEventType.PitchRefined:
                    return who + " の" + PitchJp(n) + "が良くなった";
                case GrowthEventType.BigStage:
                    return who + " が大舞台で化けた（" + GainsText(n) + "）";
                case GrowthEventType.Breakthrough:
                    return who + " がコツを掴んだ（" + GainsText(n) + "）";
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
        /// 上昇の数値は engine が通知に載せた<b>実際に動いた量</b>から作る（issue #694。係数の値をそのまま
        /// 出すと、上限で止まったときや2つ上がったときに中身と食い違う）。
        /// </summary>
        public static PresentedEffect Effect(GrowthNotice n, GrowthEventCoefficients c)
        {
            var who = n.Player.Name;
            var ability = n.Ability.HasValue ? AbilityLabels.Jp(n.Ability.Value) : "能力";
            switch (n.Type)
            {
                case GrowthEventType.Awakening:
                case GrowthEventType.GrandAwakening:
                case GrowthEventType.BigStage:
                case GrowthEventType.Breakthrough:
                    return PresentedEffect.Good(who + " の" + GainsSentence(n) + " 上がった！");
                case GrowthEventType.GrowthSurge:
                    // その場では能力が動かない＝数字は伸びしろの話として書く（上限も上がった分だけ添える）。
                    return PresentedEffect.Good(who + " の" + DomainJp(n.Domain) + "の伸びしろが広がった！"
                                                + (n.CapLift > 0 ? "（才能上限 ＋" + n.CapLift + "）" : ""));
                case GrowthEventType.NewPitch:
                    return PresentedEffect.Good(who + " が " + PitchJp(n) + " を覚えた！");
                case GrowthEventType.PitchRefined:
                    return PresentedEffect.Good(who + " の " + PitchJp(n) + " のランクが上がった！");
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

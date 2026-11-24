using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 投手の素質の見出し（見立て→外部コーチ＝issue #1523）の文言の単一ソース。通知フィードの1行・ノベルのイベントID・
    /// 会話の最終行に出す結果行をここだけで決める（<see cref="SkillChangeText"/> と同じ作法）。
    /// 「投手に向いている」と示すのは、ポジションを勧めない方針の例外（オーナー判断 2026-11-24・この出来事だけ）。
    /// 選手詳細には常時は出さない＝出口は結果行と通知フィードだけ。
    /// </summary>
    public static class PitcherTalentText
    {
        /// <summary>data/events.yaml のイベントID。</summary>
        public static string EventId(PitcherTalentNotice n)
            => n.Stage == PitcherTalentStage.Discovered ? "pitcher_talent_discovered" : "pitcher_talent_hint";

        /// <summary>通知フィードのタグ（見立ては発見、見出されたのは成長）。</summary>
        public static string Tag(PitcherTalentNotice n) => n.Stage == PitcherTalentStage.Discovered ? "成長" : "発見";

        /// <summary>通知フィードに残す1行。</summary>
        public static string Feed(PitcherTalentNotice n)
            => n.Stage == PitcherTalentStage.Discovered
                ? n.Player.Name + " が外部コーチに見出された"
                : n.Player.Name + " は投手に向いているかもしれない";

        /// <summary>会話の最終行に出す結果行。見立ては断定しない（見立てだけでは伸びは変わらない）。</summary>
        public static PresentedEffect Effect(PitcherTalentNotice n)
            => n.Stage == PitcherTalentStage.Discovered
                ? PresentedEffect.Good(n.Player.Name + " の投手としての伸びが大きくなった！")
                : PresentedEffect.Good(n.Player.Name + " は投手に向いているかもしれない（見立て）");
    }
}

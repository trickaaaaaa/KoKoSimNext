using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// ライブ観戦の表示合成（設計書06・不変条件「数値はエンジン集計から引く」）。エンジンのスナップショット
    /// （今日の成績・左右・守備位置）に、Shell 側の単一ソースから通算・背番号・調子を <see cref="Player.SourceId"/>
    /// をキーに join する。自校のみ join 成立（相手校は SourceId=null で背番号フォールバック・調子非表示・通算「—」）。
    /// UI 側で数値を再計算しない（表示専用）。
    /// </summary>
    public sealed class MatchLiveStatsProvider
    {
        private readonly Dictionary<int, DevelopingPlayer> _byId = new();
        private readonly PlayerStatStore _stats;

        public MatchLiveStatsProvider()
        {
            foreach (var p in RosterService.Roster) _byId[p.Id] = p;
            _stats = GameSession.Current.Stats;
        }

        /// <summary>調子（自校のみ。相手校など不明は null＝表情顔を出さない）。</summary>
        public Condition? ConditionOf(int? sourceId) =>
            sourceId is int id && _byId.TryGetValue(id, out var dp)
                ? FormModel.Quantize(dp.ConditionValue)
                : (Condition?)null;

        /// <summary>公式戦通算の打撃成績（試合数0＝未出場は null）。</summary>
        public BattingStatLine? CareerBatting(int? sourceId)
        {
            var s = sourceId is int id ? _stats.Career.Get(id) : null;
            return s != null && s.Batting.Games > 0 ? s.Batting : null;
        }

        /// <summary>公式戦通算の投手成績（登板0は null）。</summary>
        public PitchingStatLine? CareerPitching(int? sourceId)
        {
            var s = sourceId is int id ? _stats.Career.Get(id) : null;
            return s != null && s.Pitching.Games > 0 ? s.Pitching : null;
        }

        // ── 表示用の短縮ラベル（部品側でなく共通の書式を1箇所に集約） ──

        /// <summary>守備位置の1文字略記（投捕一二三遊左中右）。</summary>
        public static string PosAbbr(FieldPosition pos) => pos switch
        {
            FieldPosition.Pitcher => "投",
            FieldPosition.Catcher => "捕",
            FieldPosition.FirstBase => "一",
            FieldPosition.SecondBase => "二",
            FieldPosition.ThirdBase => "三",
            FieldPosition.Shortstop => "遊",
            FieldPosition.LeftField => "左",
            FieldPosition.CenterField => "中",
            FieldPosition.RightField => "右",
            _ => "—",
        };

        public static string ThrowsLabel(Handedness h) => (h == Handedness.Left ? "左" : "右") + "投";
        public static string BatsLabel(Handedness h) =>
            (h == Handedness.Switch ? "両" : h == Handedness.Left ? "左" : "右") + "打";

        /// <summary>打率表記（.342／通算なしは「—」）。</summary>
        public static string Avg(BattingStatLine? b) => b is null ? "—" : FormatAvg(b.Average);
        /// <summary>打率表記（.342 形式）。1試合ぶんの打率（試合結果画面）もこの書式に揃える。</summary>
        public static string Avg(double average) => FormatAvg(average);
        /// <summary>防御率表記（2.14／登板なしは「—」）。</summary>
        public static string Era(PitchingStatLine? p) => p is null ? "—" : p.Era.ToString("0.00");

        /// <summary>今日の成績（例 2-1、打点があれば 2-1 ①）。</summary>
        public static string TodayLine(int atBats, int hits, int rbi)
        {
            var s = atBats + "-" + hits;
            if (rbi > 0) s += " " + CircledRbi(rbi);
            return s;
        }

        private static string FormatAvg(double avg)
        {
            // .342 形式（先頭0を省く）。
            var t = avg.ToString("0.000");
            return t.StartsWith("0") ? t.Substring(1) : t;
        }

        // （背番号は Player.UniformNumber をエンジン集計から引くため、UI 側の位置フォールバックは廃止）

        // 打点のマル数字（①〜⑳、超過は (n)）。
        private static string CircledRbi(int n)
        {
            const string circled = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";
            return n >= 1 && n <= circled.Length ? circled[n - 1].ToString() : "(" + n + ")";
        }
    }
}

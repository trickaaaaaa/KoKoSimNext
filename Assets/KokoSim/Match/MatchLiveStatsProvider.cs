using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// ライブ観戦の表示合成（設計書06・不変条件「数値はエンジン集計から引く」）。エンジンのスナップショット
    /// （今日の成績・左右・守備位置・調子の真値）に、Shell 側の単一ソースから通算成績を <see cref="Player.SourceId"/>
    /// をキーに join する（相手校は SourceId=null で通算「—」）。
    /// 調子は自校=常に真値、相手校=監督の育成眼に応じた誤認モデル（<see cref="FormModel.Observe"/>）を通す
    /// （設計書02 §3.3, issue #47）。観測ノイズは試合ごとに固定した RNG を選手名で Fork するため、
    /// 同一試合中は同じ見え方になる（決定論・不変条件#2）。UI 側で数値を再計算しない（表示専用）。
    /// </summary>
    public sealed class MatchLiveStatsProvider
    {
        private static int _seq;

        private readonly PlayerStatStore _stats;
        private readonly IRandomSource _observeRng;
        private readonly FormCoefficients _formCoeff = new();

        public MatchLiveStatsProvider()
        {
            _stats = GameSession.Current.Stats;
            // 試合ごとに独立した決定論ストリーム（週・年度・生成連番から導出。GameSession.ApplyMatchInjuries と同型）。
            _observeRng = new Xoshiro256Random(
                0x0BF0_0000UL ^ (ulong)(GameClock.YearIndex * 10000 + GameClock.Week * 100 + (++_seq)));
        }

        /// <summary>
        /// 調子（自校=常に真値。相手校=育成眼に応じた誤認あり）。<paramref name="name"/> は相手校の観測ノイズを
        /// 選手ごとに Fork で固定するためのキー（Fork は親ストリームを進めないため、同一選手は毎回同じ結果になる）。
        /// </summary>
        public Condition ConditionOf(int? sourceId, double conditionValue, string name) =>
            sourceId is int
                ? FormModel.Quantize(conditionValue)
                : FormModel.Observe(conditionValue, ManagerService.Manager.TalentEye,
                    _observeRng.Fork(NameStreamId(name)), _formCoeff);

        // 選手名 → Fork streamId（FNV-1a 64bit）。相手校の生成選手には SourceId が無いため、
        // 名前を安定キーとして使う（同一試合の同一選手なら常に同じ streamId）。
        private static ulong NameStreamId(string name)
        {
            var hash = 0xcbf29ce484222325UL;
            foreach (var ch in name)
            {
                hash ^= ch;
                hash *= 0x100000001b3UL;
            }
            return hash;
        }

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

        /// <summary>守備位置の1文字略記（投捕一二三遊左中右・DHは指）。</summary>
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
            FieldPosition.DesignatedHitter => "指",
            _ => "—",
        };

        public static string ThrowsLabel(Handedness h) => HandednessLabels.Throws(h);
        public static string BatsLabel(Handedness h) => HandednessLabels.Bats(h);

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

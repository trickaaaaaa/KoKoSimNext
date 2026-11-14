using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// タイム（試合中の中断ポップアップ）の ViewModel。表示する値だけを持つ＝View は詰め替えず描くだけ。
    /// 値は MatchLiveController がエンジンの観測（<c>LiveAtBat</c> / <c>MatchLiveSnapshot</c> /
    /// <c>MatchProgression</c> の伝令）と Shell の単一ソース（通算成績・調子の誤認）から集めて <see cref="Build"/> へ渡す。
    /// 規則（伝令の枠など）はここで決めない＝エンジンから受け取った値をそのまま並べる。
    /// </summary>
    public sealed class TimeoutPopupModel
    {
        /// <summary>対戦欄の1行（打者／投手）。</summary>
        public sealed class PlayerLine
        {
            /// <summary>背番号（空＝番号なし）。</summary>
            public string Number = "";
            public string Name = "";
            /// <summary>「1番 三塁　3年 右投左打」／「3年 右投右打」。</summary>
            public string Detail = "";
            /// <summary>右端の成績（打者＝打率・本、投手＝球数）。</summary>
            public IReadOnlyList<(string Label, string Value)> Stats = new List<(string, string)>();
            public Condition Condition = Condition.Normal;
            /// <summary>調子が推定（相手校＝監督の目で見た値）か。</summary>
            public bool ConditionEstimated;
        }

        // ── 状況欄 ──
        public int Inning;
        public bool IsTop;
        /// <summary>アウト数（0〜2）。</summary>
        public int Outs;
        public bool OnFirst, OnSecond, OnThird;
        public string AwayName = "", HomeName = "";
        public int AwayScore, HomeScore;
        public bool ManagerIsAway;

        /// <summary>自校がいま攻撃中か（false＝守備中）。</summary>
        public bool ManagerOnOffense;

        // ── 対戦欄（null＝打席の切れ目で対戦がまだ無い） ──
        public PlayerLine Batter;
        public PlayerLine Pitcher;

        // ── 伝令 ──
        /// <summary>いまの回の伝令の枠（規定回3・延長1。エンジンの <c>TimeoutsPerPeriod</c>）。</summary>
        public int TimeoutCap = 3;
        public int OffenseTimeoutsLeft, DefenseTimeoutsLeft;
        /// <summary>「伝令を送る」を押せるか（残り・解決中・この中断で送信済みをまとめた結果）。</summary>
        public bool TimeoutEnabled;
        /// <summary>この中断で既に伝令を送ったか。</summary>
        public bool TimeoutSent;

        /// <summary>選手交代・選手能力・スキップを押せるか（打席の解決中は伏せる）。</summary>
        public bool ActionsEnabled = true;

        /// <summary>いまの攻守の伝令の残り。</summary>
        public int CurrentTimeoutsLeft => ManagerOnOffense ? OffenseTimeoutsLeft : DefenseTimeoutsLeft;
        /// <summary>もう一方（使えない側）の伝令の残り。</summary>
        public int OtherTimeoutsLeft => ManagerOnOffense ? DefenseTimeoutsLeft : OffenseTimeoutsLeft;

        /// <summary>打者行の詳細「1番 三塁　3年 右投左打」。0・不明の項目は飛ばす。</summary>
        public static string BatterDetail(int order, FieldPosition position, int grade, Handedness throws, Handedness bats)
        {
            var head = new List<string>();
            if (order >= 1) head.Add(order + "番");
            var pos = MatchLiveStatsProvider.PosJp(position);
            if (pos.Length > 0) head.Add(pos);
            return Join(string.Join(" ", head), Profile(grade, throws, bats));
        }

        /// <summary>投手行の詳細「3年 右投右打」。</summary>
        public static string PitcherDetail(int grade, Handedness throws, Handedness bats) => Profile(grade, throws, bats);

        private static string Profile(int grade, Handedness throws, Handedness bats)
        {
            var hands = HandednessLabels.Combined(throws, bats);
            return grade > 0 ? grade + "年 " + hands : hands;
        }

        // 打順・位置と学年・投打の間は全角空白（モックの区切り）。
        private static string Join(string a, string b)
            => a.Length == 0 ? b : b.Length == 0 ? a : a + "　" + b;
    }
}

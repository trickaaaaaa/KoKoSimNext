using System.Collections.Generic;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>チーム成績表の1行（項目名＋3スコープの値）。値は整形済み文字列（未計上は「—」）。</summary>
    public sealed class TeamStatRow
    {
        public string Label = "";
        public string Career = "—";
        public string Official = "—";
        public string Tournament = "—";
    }

    /// <summary>チーム内ランキングの1件（該当なしは Empty＝true で「—」を出す）。</summary>
    public sealed class RankEntry
    {
        public string Name = "";
        public string Value = "";
        public bool Empty = true;
    }

    /// <summary>チーム内ランキングの1部門（打率・本塁打…）。<see cref="Top"/> は常に固定長で埋める。</summary>
    public sealed class RankRow
    {
        /// <summary>区分見出し（「打撃」「投手」）。直前の行と変わったときだけ描く。</summary>
        public string Section = "";
        public string Label = "";
        public List<RankEntry> Top = new List<RankEntry>();
    }

    /// <summary>
    /// 自校のチーム成績・チーム内ランキングを組み立てる（ホーム画面の情報構成・2026-07-21）。
    /// 集計元は <see cref="GameSession"/> が持つ <see cref="PlayerStatStore"/>（自校選手のみ・3スコープ）。
    /// エンジンにはチーム集計APIが無いので、選手成績の合算をここ1箇所に集約する
    /// （記録画面が出来たら同じサービスを引く＝2箇所目のコピーを作らない）。
    /// 表示専用＝ゲームバランスには影響しない。UnityEngine 非依存。
    /// </summary>
    public static class TeamStatsService
    {
        private const string Dash = "—";

        /// <summary>ランキングに出す人数（1〜2位）。打撃／投手を横2列に並べるぶん枠幅が半分になるため2位まで。</summary>
        public const int TopCount = 2;

        // ===== チーム成績（合算） =====

        /// <summary>チーム成績表（打率／本塁打／打点／防御率／WHIP × 通算・公式戦・今大会）。</summary>
        public static List<TeamStatRow> TeamTotals()
        {
            var store = GameSession.Current.Stats;
            var career = Aggregate(store.Career);
            var official = Aggregate(store.Official);
            var current = Aggregate(store.CurrentTournament);

            var rows = new List<TeamStatRow>();
            rows.Add(Row("打率", career.Avg, official.Avg, current.Avg));
            rows.Add(Row("本塁打", career.HomeRuns, official.HomeRuns, current.HomeRuns));
            rows.Add(Row("打点", career.Rbi, official.Rbi, current.Rbi));
            rows.Add(Row("防御率", career.Era, official.Era, current.Era));
            rows.Add(Row("WHIP", career.Whip, official.Whip, current.Whip));
            return rows;
        }

        private static TeamStatRow Row(string label, string a, string b, string c)
            => new TeamStatRow { Label = label, Career = a, Official = b, Tournament = c };

        /// <summary>1スコープぶんの合算結果（整形済み）。</summary>
        private struct TeamTotal
        {
            public string Avg, HomeRuns, Rbi, Era, Whip;
        }

        private static TeamTotal Aggregate(StatBook book)
        {
            int atBats = 0, hits = 0, hr = 0, rbi = 0;
            int outs = 0, runs = 0, pHits = 0, walks = 0;

            foreach (var kv in book.Players)
            {
                var b = kv.Value.Batting;
                atBats += b.AtBats; hits += b.Hits; hr += b.HomeRuns; rbi += b.Rbi;
                var p = kv.Value.Pitching;
                outs += p.Outs; runs += p.Runs; pHits += p.Hits; walks += p.Walks;
            }

            return new TeamTotal
            {
                Avg = atBats > 0 ? Avg3((double)hits / atBats) : Dash,
                HomeRuns = atBats > 0 ? hr.ToString() : Dash,
                Rbi = atBats > 0 ? rbi.ToString() : Dash,
                Era = outs > 0 ? (runs * 27.0 / outs).ToString("0.00") : Dash,
                Whip = outs > 0 ? ((pHits + walks) * 3.0 / outs).ToString("0.00") : Dash,
            };
        }

        // ===== チーム内ランキング（通算スコープ） =====

        /// <summary>
        /// チーム内ランキング（通算）。打率・防御率は規定（チーム試合数基準）を満たす選手のみ。
        /// 該当者が居ない枠は Empty のまま返す＝呼び出し側は常に固定行数を描ける。
        /// </summary>
        public static List<RankRow> Rankings()
        {
            var book = GameSession.Current.Stats.Career;
            var names = NameIndex();

            // 規定の基準になるチーム試合数＝選手の最大出場試合数（チーム試合数のカウンタが無いための代用）。
            var teamGames = 0;
            foreach (var kv in book.Players)
            {
                if (kv.Value.Batting.Games > teamGames) teamGames = kv.Value.Batting.Games;
                if (kv.Value.Pitching.Games > teamGames) teamGames = kv.Value.Pitching.Games;
            }
            var paNeeded = System.Math.Max(1, teamGames * 2);       // 規定打席＝チーム試合数×2
            var outsNeeded = System.Math.Max(1, teamGames * 3);     // 規定投球回＝チーム試合数×1

            var rows = new List<RankRow>();

            // 打撃。率系（打率・出塁率・OPS）は規定打席、数系（本塁打・打点・安打）は1以上で載せる。
            rows.Add(Build(book, names, "打撃", "打率", descending: true,
                qualify: s => s.Batting.PlateAppearances >= paNeeded && s.Batting.AtBats > 0,
                key: s => s.Batting.Average,
                text: s => Avg3(s.Batting.Average)));
            rows.Add(Build(book, names, "打撃", "本塁打", descending: true,
                qualify: s => s.Batting.HomeRuns > 0,
                key: s => s.Batting.HomeRuns,
                text: s => s.Batting.HomeRuns.ToString()));
            rows.Add(Build(book, names, "打撃", "打点", descending: true,
                qualify: s => s.Batting.Rbi > 0,
                key: s => s.Batting.Rbi,
                text: s => s.Batting.Rbi.ToString()));
            rows.Add(Build(book, names, "打撃", "安打", descending: true,
                qualify: s => s.Batting.Hits > 0,
                key: s => s.Batting.Hits,
                text: s => s.Batting.Hits.ToString()));
            rows.Add(Build(book, names, "打撃", "出塁率", descending: true,
                qualify: s => s.Batting.PlateAppearances >= paNeeded && s.Batting.AtBats > 0,
                key: s => s.Batting.Obp,
                text: s => Avg3(s.Batting.Obp)));
            rows.Add(Build(book, names, "打撃", "OPS", descending: true,
                qualify: s => s.Batting.PlateAppearances >= paNeeded && s.Batting.AtBats > 0,
                key: s => s.Batting.Ops,
                text: s => Avg3(s.Batting.Ops)));
            // 盗塁は枠だけ用意する。エンジンが選手別の盗塁を集計していないため常に空欄
            // （TacticsTally は GameResult にチーム単位でしか無く、選手成績へ畳み込まれていない）。
            rows.Add(Placeholder("打撃", "盗塁"));

            // 投手。率系（防御率・WHIP）は規定投球回、数系（勝利・奪三振・投球回）は1以上。
            rows.Add(Build(book, names, "投手", "防御率", descending: false,
                qualify: s => s.Pitching.Outs >= outsNeeded,
                key: s => s.Pitching.Era,
                text: s => s.Pitching.Era.ToString("0.00")));
            rows.Add(Build(book, names, "投手", "勝利", descending: true,
                qualify: s => s.Pitching.Wins > 0,
                key: s => s.Pitching.Wins,
                text: s => s.Pitching.Wins.ToString()));
            rows.Add(Build(book, names, "投手", "奪三振", descending: true,
                qualify: s => s.Pitching.StrikeOuts > 0,
                key: s => s.Pitching.StrikeOuts,
                text: s => s.Pitching.StrikeOuts.ToString()));
            rows.Add(Build(book, names, "投手", "WHIP", descending: false,
                qualify: s => s.Pitching.Outs >= outsNeeded,
                key: s => s.Pitching.Whip,
                text: s => s.Pitching.Whip.ToString("0.00")));
            rows.Add(Build(book, names, "投手", "投球回", descending: true,
                qualify: s => s.Pitching.Outs > 0,
                key: s => s.Pitching.Outs,
                text: s => s.Pitching.InningsText));
            return rows;
        }

        /// <summary>データ源が無い部門の空枠（常に「—」）。行が消えないよう固定長で埋める。</summary>
        private static RankRow Placeholder(string section, string label)
        {
            var row = new RankRow { Section = section, Label = label };
            for (var i = 0; i < TopCount; i++) row.Top.Add(new RankEntry { Empty = true });
            return row;
        }

        private static RankRow Build(StatBook book, Dictionary<int, string> names, string section, string label,
            bool descending,
            System.Func<PlayerStats, bool> qualify,
            System.Func<PlayerStats, double> key,
            System.Func<PlayerStats, string> text)
        {
            var pool = new List<PlayerStats>();
            foreach (var kv in book.Players) if (qualify(kv.Value)) pool.Add(kv.Value);

            // 同値は選手ID昇順で確定させる（決定論・順序が揺れない）。
            pool.Sort((x, y) =>
            {
                var c = key(x).CompareTo(key(y));
                if (!descending) c = -c;
                if (c != 0) return -c;
                return x.SourceId.CompareTo(y.SourceId);
            });

            var row = new RankRow { Section = section, Label = label };
            for (var i = 0; i < TopCount; i++)
            {
                if (i < pool.Count)
                {
                    var s = pool[i];
                    string name;
                    row.Top.Add(new RankEntry
                    {
                        Name = names.TryGetValue(s.SourceId, out name) ? name : "?",
                        Value = text(s),
                        Empty = false,
                    });
                }
                else
                {
                    row.Top.Add(new RankEntry { Empty = true });
                }
            }
            return row;
        }

        /// <summary>成績の帰属キー（DevelopingPlayer.Id）→ 表示名。引退者も引けるよう Roster 全体から作る。</summary>
        private static Dictionary<int, string> NameIndex()
        {
            var map = new Dictionary<int, string>();
            foreach (var p in RosterService.Roster) map[p.Id] = p.Name;
            return map;
        }

        // ===== 整形 =====

        /// <summary>野球慣行の3桁表記（0.312 → .312、1.000 はそのまま）。</summary>
        private static string Avg3(double v)
        {
            var s = v.ToString("0.000");
            return s.StartsWith("0.") ? s.Substring(1) : s;
        }
    }
}

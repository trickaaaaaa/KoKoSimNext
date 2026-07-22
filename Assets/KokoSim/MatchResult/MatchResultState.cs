using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;
using KokoSim.Unity.Match;

namespace KokoSim.Unity.MatchResult
{
    /// <summary>イニングスコアの1行（校名／各回の得点／計・安打・失策）。</summary>
    public sealed class MatchResultLineRow
    {
        public string TeamName = "";
        public bool IsOwn;                       // 自校の行（校名の強調に使う）
        public List<string> Innings = new();     // 各回の得点。攻撃しなかった回は「X」
        public string Runs = "0", Hits = "0", Errors = "0";
    }

    /// <summary>野手成績の1行（守備位置／名前／打数〜三振／打率）。</summary>
    public sealed class MatchResultBattingRow
    {
        public string Pos = "";
        public string Name = "";
        public string[] Numbers = new string[8];   // 打 安 二 三 本 点 四 三
        public string Average = "";
    }

    /// <summary>投手成績の1行（勝敗マーク／名前／球数／投球回／奪三振／失点／四球）。</summary>
    public sealed class MatchResultPitchingRow
    {
        public string Mark = "";        // 勝 / 敗 / 空
        public string Name = "";
        public string Pitches = "0";
        public string Innings = "0";
        public string StrikeOuts = "0";
        public string Runs = "0";
        public string Walks = "0";
    }

    /// <summary>打席結果グリッドの1行（打者×打席番号）。</summary>
    public sealed class MatchResultPaRow
    {
        public string Order = "";
        public string Name = "";
        public List<string> Cells = new();       // 打席番号1..n の結果テキスト（未到達は空）
        public List<bool> Reached = new();       // 出塁した打席か（淡色表示の切替）
    }

    /// <summary>片チームぶんの表（打撃／投手／打席結果）。左＝自校・右＝相手で同じ構成を並べる。</summary>
    public sealed class MatchResultSideView
    {
        public string TeamName = "";
        public string SideCaption = "";          // 自校 / 相手
        public bool IsOwn;
        public List<MatchResultBattingRow> Batting = new();
        public List<MatchResultPitchingRow> Pitching = new();
        public List<MatchResultPaRow> PaRows = new();
        public int PaColumns;                     // 打席結果グリッドの列数（最大打席番号）
    }

    /// <summary>試合結果画面（案B: 左右2カラム）の表示モデル。</summary>
    public sealed class MatchResultView
    {
        public string OutcomeText = "";           // 勝利 / 敗戦 / 引き分け
        public bool ManagerWon;
        public string ScoreText = "";             // 「桜丘 8 - 2 北都大付属」
        public List<string> InningHeaders = new();
        public MatchResultLineRow AwayLine = new();
        public MatchResultLineRow HomeLine = new();
        public MatchResultSideView Own = new();
        public MatchResultSideView Opponent = new();
    }

    /// <summary>
    /// 試合結果（<see cref="GameResult"/>）→ 試合結果画面の表示モデルへの変換（issue #13・案B）。
    /// 表示専用＝ここでは数値を作らず、エンジンの集計をそのまま文字列へ整形するだけ（不変条件#2 決定論に無影響）。
    /// 打席グリッドは engine の純関数 <see cref="BoxScoreGrid"/>、勝敗投手は
    /// <see cref="DecisionOfRecord.ResolveIndices"/> を使う（判定ロジックをUI側に持たない）。
    /// </summary>
    public static class MatchResultState
    {
        public static MatchResultView Build(GameResult r, bool managerIsAway, string awayName, string homeName)
        {
            var ownRuns = managerIsAway ? r.AwayRuns : r.HomeRuns;
            var oppRuns = managerIsAway ? r.HomeRuns : r.AwayRuns;
            var ownName = managerIsAway ? awayName : homeName;
            var oppName = managerIsAway ? homeName : awayName;

            var (awayWin, awayLose, homeWin, homeLose) = DecisionOfRecord.ResolveIndices(r);

            var v = new MatchResultView
            {
                OutcomeText = r.Tied ? "引き分け" : ownRuns > oppRuns ? "勝利" : "敗戦",
                ManagerWon = !r.Tied && ownRuns > oppRuns,
                ScoreText = ownName + "  " + ownRuns + " - " + oppRuns + "  " + oppName
                    + (r.MercyEnded ? $"（{r.InningsPlayed}回コールド）" : ""),
                AwayLine = LineRow(awayName, isOwn: managerIsAway, r.AwayRuns, r.AwayHits, r.AwayErrors),
                HomeLine = LineRow(homeName, isOwn: !managerIsAway, r.HomeRuns, r.HomeHits, r.HomeErrors),
                Own = Side(r, managerIsAway, ownName, "自校", isOwn: true,
                    managerIsAway ? awayWin : homeWin, managerIsAway ? awayLose : homeLose),
                Opponent = Side(r, !managerIsAway, oppName, "相手", isOwn: false,
                    managerIsAway ? homeWin : awayWin, managerIsAway ? homeLose : awayLose),
            };

            // イニング列は両軍の長い方に合わせる（延長は列を伸ばす）。
            var innings = r.AwayLineScore.Count > r.HomeLineScore.Count ? r.AwayLineScore.Count : r.HomeLineScore.Count;
            if (innings < 9) innings = 9;
            for (var i = 1; i <= innings; i++) v.InningHeaders.Add(i.ToString());
            FillInnings(v.AwayLine, r.AwayLineScore, innings);
            FillInnings(v.HomeLine, r.HomeLineScore, innings);
            return v;
        }

        private static MatchResultLineRow LineRow(
            string name, bool isOwn, int runs, int hits, int errors)
            => new()
            {
                TeamName = name, IsOwn = isOwn,
                Runs = runs.ToString(), Hits = hits.ToString(), Errors = errors.ToString(),
            };

        // 攻撃しなかった回（後攻のサヨナラ・9回裏不要）は「X」。回数が足りない分は空欄にしない。
        private static void FillInnings(MatchResultLineRow row, IReadOnlyList<int> score, int innings)
        {
            for (var i = 0; i < innings; i++)
                row.Innings.Add(i < score.Count ? score[i].ToString() : "X");
        }

        private static MatchResultSideView Side(
            GameResult r, bool isAway, string teamName, string caption, bool isOwn, int? winIndex, int? loseIndex)
        {
            var batting = isAway ? r.AwayBatting : r.HomeBatting;
            var pitching = isAway ? r.AwayPitching : r.HomePitching;

            var side = new MatchResultSideView { TeamName = teamName, SideCaption = caption, IsOwn = isOwn };

            foreach (var b in batting)
                side.Batting.Add(new MatchResultBattingRow
                {
                    Pos = MatchLiveStatsProvider.PosAbbr(b.Position),
                    Name = b.Name,
                    Numbers = new[]
                    {
                        b.AtBats.ToString(), b.Hits.ToString(), b.Doubles.ToString(), b.Triples.ToString(),
                        b.HomeRuns.ToString(), b.Rbi.ToString(), b.Walks.ToString(), b.StrikeOuts.ToString(),
                    },
                    Average = MatchLiveStatsProvider.Avg(b.Average),
                });

            for (var i = 0; i < pitching.Count; i++)
            {
                var p = pitching[i];
                side.Pitching.Add(new MatchResultPitchingRow
                {
                    Mark = i == winIndex ? "勝" : i == loseIndex ? "敗" : "",
                    Name = p.Name,
                    Pitches = p.Pitches.ToString(),
                    Innings = p.InningsText,
                    StrikeOuts = p.StrikeOuts.ToString(),
                    Runs = p.Runs.ToString(),
                    Walks = p.Walks.ToString(),
                });
            }

            var grid = BoxScoreGrid.Build(r.Log, isAway);
            side.PaColumns = BoxScoreGrid.MaxPaIndex(grid);
            foreach (var g in grid)
            {
                var row = new MatchResultPaRow
                {
                    Order = g.Order > 0 ? g.Order.ToString() : "",
                    Name = g.BatterName,
                };
                for (var i = 0; i < side.PaColumns; i++) { row.Cells.Add(""); row.Reached.Add(false); }
                foreach (var c in g.Cells)
                {
                    var idx = c.PaIndex - 1;
                    if (idx < 0 || idx >= side.PaColumns) continue;
                    row.Cells[idx] = ResultJp(c.Result);
                    row.Reached[idx] = Reached(c.Result);
                }
                side.PaRows.Add(row);
            }
            return side;
        }

        /// <summary>打席結果の短縮表記（グリッドの1マスに収まる長さ）。</summary>
        public static string ResultJp(PlateAppearanceResult r) => r switch
        {
            PlateAppearanceResult.Strikeout => "三振",
            PlateAppearanceResult.Walk => "四球",
            PlateAppearanceResult.HitByPitch => "死球",
            PlateAppearanceResult.Single => "安打",
            PlateAppearanceResult.Double => "二塁打",
            PlateAppearanceResult.Triple => "三塁打",
            PlateAppearanceResult.HomeRun => "本塁打",
            PlateAppearanceResult.ReachedOnError => "失策",
            _ => "凡打",
        };

        /// <summary>出塁した打席か（グリッドで濃く出す。凡打・三振は淡色）。</summary>
        private static bool Reached(PlateAppearanceResult r)
            => r is not (PlateAppearanceResult.Strikeout or PlateAppearanceResult.InPlayOut);
    }
}

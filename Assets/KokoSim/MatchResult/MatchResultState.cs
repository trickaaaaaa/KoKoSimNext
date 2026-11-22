using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Stats;
using KokoSim.Unity.Match;

namespace KokoSim.Unity.MatchResult
{
    /// <summary>守備位置チップの色の系統（試合画面の左右のチーム欄と同じ5系統＋途中出場の「打」「走」）。</summary>
    public enum MatchResultPosKind { Pitcher, Catcher, Infield, Outfield, Dh, Entry }

    /// <summary>打席の札の見せ方（GameResult.mock の表）。</summary>
    public enum MatchResultPaKind { Hit, HomeRun, Walk, Error, Other, None }

    /// <summary>イニングスコアの1行。</summary>
    public sealed class MatchResultLineRow
    {
        public string TeamName = "";
        public bool Won;                          // 勝ったチーム（校名を墨で出す。引き分けはどちらも false）
        public List<int?> Innings = new();        // 各回の得点。null＝攻撃しなかった回（「×」）
        public int Runs, Hits, Errors;
    }

    /// <summary>長打の行の1項目（本塁打／三塁打／二塁打）。学校ごとに名字をまとめる。</summary>
    public sealed class MatchResultXbhGroup
    {
        public string Label = "";
        public List<(string Names, string School)> Entries = new();   // 「北村、三宅」「北」
    }

    /// <summary>打撃成績の1行（スタメン・途中出場・計）。</summary>
    public sealed class MatchResultBattingRow
    {
        public int Order;                          // 0＝空欄（途中出場・計）
        public string Pos = "";
        public MatchResultPosKind PosKind;
        public string Name = "";
        public bool IsSub;
        public int AtBats, Hits, Rbi, HomeRuns, Triples, Doubles, WalksHbp, StrikeOuts, StolenBases;
        public string Average = "";                // 「.333」。打数0は「—」
        public bool AverageDim;                    // 安打0（薄く出す）
    }

    /// <summary>投手成績の1行（登板順）。</summary>
    public sealed class MatchResultPitchingRow
    {
        public string Name = "";
        public string Innings = "0";               // 「7 1/3」「8」
        public int Pitches, Hits, StrikeOuts, WalksHbp, Runs;
    }

    /// <summary>打席結果の1マス。</summary>
    public sealed class MatchResultPaCell
    {
        public string Text = "";
        public MatchResultPaKind Kind = MatchResultPaKind.None;
    }

    /// <summary>打席結果の1行。打撃成績の行と1対1で並ぶ（途中出場も同じ位置に入る）。</summary>
    public sealed class MatchResultPaRow
    {
        public int Order;
        public string Pos = "";
        public MatchResultPosKind PosKind;
        public string Name = "";
        public List<MatchResultPaCell> Cells = new();
    }

    /// <summary>片チームぶん（打撃成績・投手成績・打席結果）。左＝自校・右＝相手。</summary>
    public sealed class MatchResultSideView
    {
        public string TeamName = "";
        public List<MatchResultBattingRow> Batting = new();
        public MatchResultBattingRow Total = new();
        public List<MatchResultPitchingRow> Pitching = new();
        public List<MatchResultPaRow> PaRows = new();
        public int PaColumns;                       // そのチームで一番多い打席数（最低4）
    }

    /// <summary>試合結果画面（GameResult.mock）の表示モデル。</summary>
    public sealed class MatchResultView
    {
        public int InningCount;                     // 実際に行った回数（延長は増え、コールドは減る）
        public MatchResultLineRow AwayLine = new();
        public MatchResultLineRow HomeLine = new();
        public List<MatchResultXbhGroup> ExtraBaseHits = new();
        public MatchResultSideView Own = new();
        public MatchResultSideView Opponent = new();
    }

    /// <summary>
    /// 試合の記録（<see cref="MatchRecord"/>）→ 試合結果画面の表示モデル。表示専用＝ここでは数値を作らず、
    /// 記録の集計を並べ替えて表示の区分を付けるだけ（不変条件#2 決定論に無影響）。
    /// 試合直後も過去の試合（戦績）も<b>同じ記録</b>から描く＝経路は1本（issue #1413）。記録は
    /// engine の <see cref="MatchRecordFactory"/> が <see cref="GameResult"/> から作る（打席の並びは
    /// <see cref="BoxScoreGrid"/>＝判定ロジックをUI側に持たない）。
    /// </summary>
    public static class MatchResultState
    {
        public const int MinPaColumns = 4;

        /// <summary>試合直後の結果（<see cref="GameResult"/>）から描く。記録を作ってから <see cref="Build(MatchRecord)"/> に渡す。</summary>
        public static MatchResultView Build(GameResult r, bool managerIsAway, string awayName, string homeName)
            => Build(MatchRecordFactory.From(r, new MatchRecord
            {
                ManagerIsAway = managerIsAway,
                OwnName = managerIsAway ? awayName : homeName,
                OpponentName = managerIsAway ? homeName : awayName,
            }));

        /// <summary>記録から描く。詳細の無い記録（スコアだけ）は開けない行なので呼ばない。</summary>
        public static MatchResultView Build(MatchRecord rec)
        {
            var away = rec.Away!;
            var home = rec.Home!;
            var ownName = rec.OwnName;
            var oppName = rec.OpponentName;
            var tied = away.Runs == home.Runs;
            var v = new MatchResultView
            {
                AwayLine = new MatchResultLineRow
                {
                    TeamName = rec.AwayName, Won = !tied && away.Runs > home.Runs,
                    Runs = away.Runs, Hits = away.Hits, Errors = away.Errors,
                },
                HomeLine = new MatchResultLineRow
                {
                    TeamName = rec.HomeName, Won = !tied && home.Runs > away.Runs,
                    Runs = home.Runs, Hits = home.Hits, Errors = home.Errors,
                },
                Own = Side(rec.ManagerIsAway ? away : home, ownName),
                Opponent = Side(rec.ManagerIsAway ? home : away, oppName),
            };

            // 列は実際に行った回数＝両軍の長い方（延長は伸び、コールドは縮む）。後攻が短い回は裏が無かった＝「×」。
            v.InningCount = System.Math.Max(away.LineScore.Count, home.LineScore.Count);
            FillInnings(v.AwayLine, away.LineScore, v.InningCount);
            FillInnings(v.HomeLine, home.LineScore, v.InningCount);

            v.ExtraBaseHits = ExtraBaseHits(
                rec.ManagerIsAway ? away.Batting : home.Batting, rec.ManagerIsAway ? home.Batting : away.Batting,
                ownName, oppName);
            return v;
        }

        private static void FillInnings(MatchResultLineRow row, IReadOnlyList<int> score, int innings)
        {
            for (var i = 0; i < innings; i++) row.Innings.Add(i < score.Count ? score[i] : (int?)null);
        }

        // ── 長打の行（本塁打→三塁打→二塁打。0人の項目は出さない。自校→相手の順に学校でまとめる） ──
        private static List<MatchResultXbhGroup> ExtraBaseHits(
            IReadOnlyList<MatchRecordBatter> own, IReadOnlyList<MatchRecordBatter> opp, string ownName, string oppName)
        {
            var (ownMark, oppMark) = SchoolMarks(ownName, oppName);
            var groups = new List<MatchResultXbhGroup>();
            Add("本塁打", b => b.HomeRuns);
            Add("三塁打", b => b.Triples);
            Add("二塁打", b => b.Doubles);
            return groups;

            void Add(string label, System.Func<MatchRecordBatter, int> count)
            {
                var g = new MatchResultXbhGroup { Label = label };
                AddSchool(g, own, count, ownMark);
                AddSchool(g, opp, count, oppMark);
                if (g.Entries.Count > 0) groups.Add(g);
            }
        }

        private static void AddSchool(MatchResultXbhGroup g, IReadOnlyList<MatchRecordBatter> lines,
            System.Func<MatchRecordBatter, int> count, string mark)
        {
            var names = new List<string>();
            foreach (var b in lines)
            {
                var n = count(b);
                if (n <= 0) continue;
                names.Add(Surname(b.Name) + (n >= 2 ? n.ToString() : ""));
            }
            if (names.Count > 0) g.Entries.Add((string.Join("、", names), mark));
        }

        /// <summary>学校の頭文字。両校の1文字目が同じなら2文字にして区別する。</summary>
        public static (string Own, string Opp) SchoolMarks(string ownName, string oppName)
        {
            static string Head(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Substring(0, System.Math.Min(n, s.Length));
            var len = Head(ownName, 1) == Head(oppName, 1) ? 2 : 1;
            return (Head(ownName, len), Head(oppName, len));
        }

        /// <summary>名字＝名前の最初の空白（全角・半角）より前。空白が無ければ名前のまま。</summary>
        public static string Surname(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var i = name.IndexOfAny(new[] { '　', ' ' });
            return i > 0 ? name.Substring(0, i) : name;
        }

        /// <summary>表示用の選手名（姓名の間の全角空白を半角へ詰める。字間は USS の letter-spacing 側）。</summary>
        private static string DisplayName(string name) => (name ?? "").Replace('　', ' ');

        // ── 片チームぶん ──
        private static MatchResultSideView Side(MatchRecordSide rec, string teamName)
        {
            var batting = rec.Batting;
            var side = new MatchResultSideView { TeamName = teamName };

            var seenOrders = new HashSet<int>();
            var total = new MatchResultBattingRow { Name = "計" };
            foreach (var b in batting)
            {
                // 同じ打順の2行目以降＝途中出場（救援投手が打席に立った行も Entry は Starter で来るので並びで判定する）。
                var isSub = b.Entry != BoxEntry.Starter || !seenOrders.Add(b.Order);
                var (pos, kind) = PosOf(b, isSub);
                var row = new MatchResultBattingRow
                {
                    Order = isSub ? 0 : b.Order, Pos = pos, PosKind = kind, Name = DisplayName(b.Name), IsSub = isSub,
                    AtBats = b.AtBats, Hits = b.Hits, Rbi = b.Rbi, HomeRuns = b.HomeRuns, Triples = b.Triples,
                    Doubles = b.Doubles, WalksHbp = b.WalksHbp, StrikeOuts = b.StrikeOuts,
                    StolenBases = b.StolenBases,
                    Average = AvgText(b.Hits, b.AtBats), AverageDim = b.Hits == 0,
                };
                side.Batting.Add(row);
                total.AtBats += row.AtBats; total.Hits += row.Hits; total.Rbi += row.Rbi;
                total.HomeRuns += row.HomeRuns; total.Triples += row.Triples; total.Doubles += row.Doubles;
                total.WalksHbp += row.WalksHbp; total.StrikeOuts += row.StrikeOuts; total.StolenBases += row.StolenBases;
            }
            total.Average = AvgText(total.Hits, total.AtBats);
            total.AverageDim = total.Hits == 0;
            side.Total = total;

            foreach (var p in rec.Pitching)
                side.Pitching.Add(new MatchResultPitchingRow
                {
                    Name = DisplayName(p.Name), Innings = p.InningsText, Pitches = p.Pitches, Hits = p.Hits,
                    StrikeOuts = p.StrikeOuts, WalksHbp = p.WalksHbp, Runs = p.Runs,
                });

            // 打席結果は打撃成績の行と1対1に並べる（打席に立たなかった代走・守備交代も「·」の行で出す）。
            // 打席の並び（打順枠ごとの通し番号）は記録（engine の BoxScoreGrid 由来）に任せ、(打順, 名前) で突き合わせる。
            var byKey = new Dictionary<(int, string), MatchRecordPaRow>();
            var maxPa = 0;
            foreach (var g in rec.PaRows)
            {
                byKey[(g.Order, g.BatterName)] = g;
                foreach (var c in g.Cells) if (c.PaIndex > maxPa) maxPa = c.PaIndex;
            }
            side.PaColumns = System.Math.Max(MinPaColumns, maxPa);
            for (var i = 0; i < batting.Count; i++)
            {
                var b = batting[i];
                var br = side.Batting[i];
                var row = new MatchResultPaRow { Order = br.Order, Pos = br.Pos, PosKind = br.PosKind, Name = br.Name };
                for (var c = 0; c < side.PaColumns; c++) row.Cells.Add(new MatchResultPaCell { Text = "·", Kind = MatchResultPaKind.None });
                if (byKey.TryGetValue((b.Order, b.Name), out var g))
                    foreach (var cell in g.Cells)
                    {
                        var idx = cell.PaIndex - 1;
                        if (idx < 0 || idx >= side.PaColumns) continue;
                        row.Cells[idx] = new MatchResultPaCell { Text = PaText(cell), Kind = PaKind(cell.Result) };
                    }
                side.PaRows.Add(row);
            }
            return side;
        }

        /// <summary>打率の表記（「.333」「1.000」。打数0は「—」）。</summary>
        public static string AvgText(int hits, int atBats)
        {
            if (atBats <= 0) return "—";
            var t = ((double)hits / atBats).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            return t.StartsWith("0") ? t.Substring(1) : t;
        }

        /// <summary>
        /// 守備位置の欄。スタメンは守備位置、途中出場は実際に就いた守備位置（代打・代走のあと守備に就いた
        /// 場合も新しい位置・監督決定 2026-11-21）。守備に就かなかった代打は「打」、代走は「走」。
        /// </summary>
        private static (string, MatchResultPosKind) PosOf(MatchRecordBatter b, bool isSub)
        {
            if (!isSub) return (MatchLiveStatsProvider.PosAbbr(b.Position), KindOf(b.Position));
            if (b.FieldedAt is { } at) return (MatchLiveStatsProvider.PosAbbr(at), KindOf(at));
            return b.Entry switch
            {
                BoxEntry.PinchHit => ("打", MatchResultPosKind.Entry),
                BoxEntry.PinchRun => ("走", MatchResultPosKind.Entry),
                _ => (MatchLiveStatsProvider.PosAbbr(b.Position), KindOf(b.Position)),
            };
        }

        private static MatchResultPosKind KindOf(FieldPosition p) => p switch
        {
            FieldPosition.Pitcher => MatchResultPosKind.Pitcher,
            FieldPosition.Catcher => MatchResultPosKind.Catcher,
            FieldPosition.FirstBase or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop
                => MatchResultPosKind.Infield,
            FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField => MatchResultPosKind.Outfield,
            _ => MatchResultPosKind.Dh,
        };

        /// <summary>
        /// 野手成績の「位」欄（試合画面の成績タブ MatchState が使う旧表記）。スタメンは守備位置、途中出場は
        /// 入り方（打＝代打／走＝代走／守＝守備交代）と、実際に守備に就いたならその位置（例: 打→左）。
        /// </summary>
        public static string PosText(BattingLine b)
        {
            if (b.Entry == BoxEntry.Starter) return MatchLiveStatsProvider.PosAbbr(b.Position);
            var mark = b.Entry switch { BoxEntry.PinchHit => "打", BoxEntry.PinchRun => "走", _ => "守" };
            return b.FieldedAt is { } at ? mark + "→" + MatchLiveStatsProvider.PosAbbr(at) : mark;
        }

        /// <summary>打席の札の文字。凡打は犠打・犠飛・併殺を分ける（監督決定 2026-11-21）。</summary>
        public static string PaText(BoxScoreGridCell c) => PaText(c.Result, c.IsSacBunt, c.IsSacFly, c.OutsThisPa >= 2);

        /// <summary><see cref="PaText(BoxScoreGridCell)"/> の記録版。</summary>
        public static string PaText(MatchRecordPa c) => PaText(c.Result, c.SacBunt, c.SacFly, c.DoublePlay);

        private static string PaText(PlateAppearanceResult result, bool sacBunt, bool sacFly, bool doublePlay) => result switch
        {
            PlateAppearanceResult.Strikeout => "三振",
            PlateAppearanceResult.Walk => "四球",
            PlateAppearanceResult.HitByPitch => "死球",
            PlateAppearanceResult.Single => "安打",
            PlateAppearanceResult.Double => "二塁打",
            PlateAppearanceResult.Triple => "三塁打",
            PlateAppearanceResult.HomeRun => "本塁打",
            PlateAppearanceResult.ReachedOnError => "失策",
            _ => sacBunt ? "犠打" : sacFly ? "犠飛" : doublePlay ? "併殺" : "凡打",
        };

        public static MatchResultPaKind PaKind(PlateAppearanceResult r) => r switch
        {
            PlateAppearanceResult.Single or PlateAppearanceResult.Double or PlateAppearanceResult.Triple => MatchResultPaKind.Hit,
            PlateAppearanceResult.HomeRun => MatchResultPaKind.HomeRun,
            PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch => MatchResultPaKind.Walk,
            PlateAppearanceResult.ReachedOnError => MatchResultPaKind.Error,
            _ => MatchResultPaKind.Other,
        };
    }
}

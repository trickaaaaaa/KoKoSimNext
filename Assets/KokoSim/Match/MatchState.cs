// ViewModel層（設計書06 §3.4 試合・高速モード）。UnityEngine 非依存。
// 自校(実ロースター)vs 相手校 を GameEngine で実行し、スコアボード＋テキスト速報を生成。
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Match
{
    /// <summary>打撃成績1行（表示用）。</summary>
    public sealed class BatRow
    {
        public string Order = "", Pos = "", Name = "";
        public int Ab, H, Rbi, Hr, Bb, So;
        public string Avg = "";
    }

    /// <summary>投手成績1行（表示用）。</summary>
    public sealed class PitRow
    {
        public string Name = "", Innings = "";
        public int H, R, So, Bb, Pitches;
    }

    public sealed class InningCol
    {
        public string Label = "1";
        public string Away = "";
        public string Home = "";
    }

    public enum FeedKind { Header, Hit, Run, Info }

    public sealed class MatchFeedLine
    {
        public string Text = "";
        public FeedKind Kind = FeedKind.Info;
    }

    public sealed class MatchView
    {
        public string AwayTeam = "北都大付属";
        public string HomeTeam = "桜丘";
        public bool Played;

        public int AwayRuns, HomeRuns;
        public int AwayHits, HomeHits, AwayErrors, HomeErrors;
        public int Innings;
        public string ResultText = "";   // 勝利! / 敗戦 / 引き分け
        public string ResultKind = "";   // win / lose / draw

        public List<InningCol> Board = new List<InningCol>();
        public List<MatchFeedLine> Feed = new List<MatchFeedLine>();

        // 出場成績（自校＝home を主に、相手＝away も）。
        public List<BatRow> HomeBat = new List<BatRow>();
        public List<BatRow> AwayBat = new List<BatRow>();
        public List<PitRow> HomePit = new List<PitRow>();
        public List<PitRow> AwayPit = new List<PitRow>();
    }

    /// <summary>
    /// 試合画面の状態。自校は実ロースターから、相手校は目標強さの生成ロースターから Team を編成し
    /// （両者とも実氏名）、GameEngine で1試合実行する。
    /// </summary>
    public sealed class MatchState
    {
        private const string HomeName = "桜丘";
        private const string AwayName = "北都大付属";

        private readonly Team _homeTeam;   // 自校（後攻）
        private readonly Team _awayTeam;   // 相手校（先攻）
        private readonly GameContext _ctx = new GameContext();

        private GameResult? _result;
        private int _gameCount;

        public MatchState()
        {
            // 自校: ホーム画面と同一 seed=42 の弱小校ロースター。
            _homeTeam = RosterTeamBuilder.Build(BuildRoster(42, 32), HomeName);
            // 相手校: やや格上（初期能力 42）の生成ロースター（実氏名）。
            _awayTeam = RosterTeamBuilder.Build(BuildRoster(7, 42), AwayName);
        }

        /// <summary>試合を1つ実行する（呼ぶたび別シードで違う展開）。</summary>
        public void PlayGame()
        {
            _gameCount++;
            _result = GameEngine.Play(_awayTeam, _homeTeam, _ctx, new Xoshiro256Random(1000UL + (ulong)_gameCount));
        }

        public MatchView BuildView()
        {
            var v = new MatchView { AwayTeam = AwayName, HomeTeam = HomeName };

            if (_result == null) return v;
            var r = _result;
            v.Played = true;
            v.AwayRuns = r.AwayRuns; v.HomeRuns = r.HomeRuns;
            v.AwayHits = r.AwayHits; v.HomeHits = r.HomeHits;
            v.AwayErrors = r.AwayErrors; v.HomeErrors = r.HomeErrors;
            v.Innings = r.InningsPlayed;

            if (r.Tied) { v.ResultText = "引き分け"; v.ResultKind = "draw"; }
            else if (r.HomeWon) { v.ResultText = "勝利！"; v.ResultKind = "win"; }
            else { v.ResultText = "敗戦"; v.ResultKind = "lose"; }

            v.Board = BuildBoard(r);
            v.Feed = BuildFeed(r);
            v.HomeBat = r.HomeBatting.Select(ToBatRow).ToList();
            v.AwayBat = r.AwayBatting.Select(ToBatRow).ToList();
            v.HomePit = r.HomePitching.Select(ToPitRow).ToList();
            v.AwayPit = r.AwayPitching.Select(ToPitRow).ToList();
            return v;
        }

        private static BatRow ToBatRow(BattingLine b) => new()
        {
            Order = b.Order.ToString(),
            Pos = PosJp(b.Position),
            Name = b.Name,
            Ab = b.AtBats, H = b.Hits, Rbi = b.Rbi, Hr = b.HomeRuns, Bb = b.Walks, So = b.StrikeOuts,
            Avg = AvgStr(b.Average, b.AtBats),
        };

        private static PitRow ToPitRow(PitchingLine p) => new()
        {
            Name = p.Name, Innings = p.InningsText,
            H = p.Hits, R = p.Runs, So = p.StrikeOuts, Bb = p.Walks, Pitches = p.Pitches,
        };

        private static string AvgStr(double avg, int ab)
        {
            if (ab == 0) return "－";
            var s = avg.ToString("0.000");
            return s.StartsWith("0") ? s.Substring(1) : s;   // 0.312 → .312
        }

        private static string PosJp(FieldPosition p)
        {
            switch (p)
            {
                case FieldPosition.Pitcher: return "投";
                case FieldPosition.Catcher: return "捕";
                case FieldPosition.FirstBase: return "一";
                case FieldPosition.SecondBase: return "二";
                case FieldPosition.ThirdBase: return "三";
                case FieldPosition.Shortstop: return "遊";
                case FieldPosition.LeftField: return "左";
                case FieldPosition.CenterField: return "中";
                case FieldPosition.RightField: return "右";
                default: return "－";
            }
        }

        // ===== スコアボード（イニング別 R, ＋ H E） =====

        private static List<InningCol> BuildBoard(GameResult r)
        {
            var board = new List<InningCol>();
            for (var i = 0; i < r.InningsPlayed; i++)
            {
                board.Add(new InningCol
                {
                    Label = (i + 1).ToString(),
                    Away = i < r.AwayLineScore.Count ? r.AwayLineScore[i].ToString() : "",
                    Home = i < r.HomeLineScore.Count ? r.HomeLineScore[i].ToString() : "Ｘ",
                });
            }
            return board;
        }

        // ===== テキスト速報（注目プレーのみ＋イニング見出し） =====

        private List<MatchFeedLine> BuildFeed(GameResult r)
        {
            var feed = new List<MatchFeedLine>();
            var curInning = -1;
            var curTop = true;

            foreach (var e in r.Log)
            {
                if (e.Inning != curInning || e.IsTop != curTop)
                {
                    curInning = e.Inning; curTop = e.IsTop;
                    var side = e.IsTop ? AwayName : HomeName;
                    feed.Add(new MatchFeedLine
                    {
                        Text = e.Inning + "回" + (e.IsTop ? "表" : "裏") + " ─ " + side + "の攻撃",
                        Kind = FeedKind.Header,
                    });
                }

                // 注目プレー（安打・四球・失策出塁・得点）のみ速報化。凡退・三振は間引く。
                var notable = e.Result.IsHit()
                    || e.Result == PlateAppearanceResult.Walk
                    || e.Result == PlateAppearanceResult.HitByPitch
                    || e.Result == PlateAppearanceResult.ReachedOnError
                    || e.RunsScored > 0;
                if (!notable) continue;

                var text = e.BatterName + "　" + ResultJp(e.Result);
                if (e.RunsScored > 0) text += "　＝ " + e.RunsScored + "点！";
                feed.Add(new MatchFeedLine
                {
                    Text = text,
                    Kind = e.RunsScored > 0 ? FeedKind.Run : FeedKind.Hit,
                });
            }

            feed.Add(new MatchFeedLine { Text = "試合終了", Kind = FeedKind.Header });
            // 描画負荷を抑えるため速報は末尾側を残して上限。
            const int maxLines = 26;
            if (feed.Count > maxLines) feed = feed.GetRange(feed.Count - maxLines, maxLines);
            return feed;
        }

        // ===== ロスター生成 =====

        private static IReadOnlyList<DevelopingPlayer> BuildRoster(ulong seed, double initMean)
        {
            var rng = new Xoshiro256Random(seed);
            var coeff = new RosterCoefficients { InitLevelMean = initMean };
            var list = new List<DevelopingPlayer>();
            for (var grade = 1; grade <= 3; grade++)
                foreach (var p in ProspectGenerator.Intake(grade, coeff, rng))
                {
                    p.Grade = grade;
                    list.Add(p);
                }
            return list;
        }

        private static string ResultJp(PlateAppearanceResult r)
        {
            switch (r)
            {
                case PlateAppearanceResult.Single: return "ヒット";
                case PlateAppearanceResult.Double: return "二塁打";
                case PlateAppearanceResult.Triple: return "三塁打";
                case PlateAppearanceResult.HomeRun: return "ホームラン";
                case PlateAppearanceResult.Walk: return "四球";
                case PlateAppearanceResult.HitByPitch: return "死球";
                case PlateAppearanceResult.ReachedOnError: return "出塁（失策）";
                case PlateAppearanceResult.Strikeout: return "三振";
                default: return "凡退";
            }
        }
    }
}

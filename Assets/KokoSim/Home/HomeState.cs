// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Home
{
    public enum FeedKind { Normal, Up, Warn, Discover }

    public sealed class FeedItem
    {
        public string When = "今週";
        public string Text = "";
        public FeedKind Kind = FeedKind.Normal;
        public string Tag = "情報";   // 成長 / 警告 / 発見 / 情報
    }

    public sealed class GradeChip
    {
        public string Grade = "D";   // S〜G
        public string Label = "";
    }

    public sealed class RosterRow
    {
        public string Name = "";
        public string Number = "";
        public string Position = "";
        public string OverallGrade = "C";
        public string Condition = "普通"; // 絶好調/好調/普通/不調/絶不調
    }

    public sealed class PlanDay
    {
        public string Day = "月";
        public string Menu = "打撃";
        public bool Match;              // 試合日
    }

    public sealed class GuidanceSlot
    {
        public string Name = "";
        public string Focus = "";
        public bool Empty;
    }

    /// <summary>ホーム画面に表示する一式（スナップショット）。</summary>
    public sealed class HomeView
    {
        public string SchoolName = "県立桜丘高校";
        public string Prefecture = "神奈川県";
        public string Badge = "桜";
        public string TeamRankGrade = "C";
        public string WeekLabel = "";
        public string CountdownLabel = "夏予選まで";
        public string CountdownValue = "";
        public string Funds = "";
        public string FameGrade = "D";
        public string TrustGrade = "C";

        public string HomeTeam = "桜丘";
        public string AwayTeam = "北都大付属";
        public string GameTag = "練習試合";
        public string HomeSub = "自校";
        public string AwaySub = "県立・強豪";
        public string GameDate = "";
        public string Venue = "自校グラウンド";
        public string Weather = "曇";

        public int Injuries;
        public string InjuredNames = "";

        public bool TournamentMode;   // 大会モード中は次戦カード/カウントダウンを大会仕様に切替える

        public int GuidanceUsed;
        public int GuidanceTotal = 3;

        public List<PlanDay> Plan = new List<PlanDay>();
        public List<RosterRow> Roster = new List<RosterRow>();
        public List<FeedItem> Feed = new List<FeedItem>();
        public List<GuidanceSlot> Guidance = new List<GuidanceSlot>();
    }

    /// <summary>
    /// ホーム画面の状態機械。純エンジンを駆動して「今週を進める」を実体化する。
    /// 週送りで練習→育成・イベントを回し、結果をフィードに流す（設計書06 §2: 通知フィード駆動）。
    /// </summary>
    public sealed class HomeState
    {
        private static readonly string[] DayNames = { "月", "火", "水", "木", "金", "土", "日" };
        private static readonly TrainingMenu[] WeekMenus =
        {
            TrainingMenu.Batting, TrainingMenu.Defense, TrainingMenu.Pitching,
            TrainingMenu.Strength, TrainingMenu.BaseRunning, TrainingMenu.Running, TrainingMenu.Rest,
        };

        private readonly IRandomSource _rng;
        private readonly SeasonCalendar _calendar = new SeasonCalendar();
        private readonly GrowthStageTable _stages = new GrowthStageTable();
        private readonly TrainingCoefficients _training = new TrainingCoefficients();
        private readonly List<DevelopingPlayer> _roster = new List<DevelopingPlayer>();
        private readonly List<FeedItem> _feed = new List<FeedItem>();

        // 現在週/年度は全画面共有の GameClock、大会モードの進行は GameSession を単一ソースとする（画面ごとに持たない）。
        private static readonly NationCoefficients NationCoeff = new NationCoefficients();
        private static readonly TournamentSchedule Schedule = new TournamentSchedule();
        private const int ManagerSchoolId = -1;      // 自校（生成校と衝突しない専用ID）
        private const int FieldPrefectureId = 13;    // 神奈川（Prefecture.Id は0基点＝JIS番号-1。#14→13, 校数≒211）
        private static KokoSim.Engine.Nation.Nation _nation;   // 出場校の母集団（決定論・初回のみ生成）

        public HomeState(ulong seed = 42)
        {
            _rng = new Xoshiro256Random(seed);
            var roster = new RosterCoefficients();
            // 3学年ぶんの部員を用意。
            for (var grade = 1; grade <= 3; grade++)
            {
                foreach (var p in ProspectGenerator.Intake(grade, roster, _rng))
                {
                    p.Grade = grade;
                    _roster.Add(p);
                }
            }
            _feed.Add(new FeedItem { When = "先週", Text = "新チームが始動した。", Kind = FeedKind.Normal, Tag = "情報" });
        }

        /// <summary>今週を進める: 全部員を1週練習させ→共有週を進め、大会開幕週なら大会モードへ入る。</summary>
        public void AdvanceWeek()
        {
            RunPracticeWeek(1.0);                       // 通常週の練習（効果1.0）
            KokoSim.Unity.Shell.GameClock.Advance();    // 共有週を進める（全画面へ反映）

            // 大会開幕週に到達したら大会モードへ遷移（要件1）。
            if (GameSession.Current.Mode == GameMode.Normal)
            {
                var kind = _calendar.TournamentStartingAt(KokoSim.Unity.Shell.GameClock.Week);
                if (kind is { } k) EnterTournament(k);
            }
        }

        /// <summary>1週ぶんの練習を適用する。extraMult は大会モード時の効果低下（&lt;1.0）に使う。</summary>
        private void RunPracticeWeek(double extraMult)
        {
            var week = KokoSim.Unity.Shell.GameClock.Week;
            var menu = WeekMenus[week % WeekMenus.Length];
            var campMult = _calendar.CampMultiplier(week, _training) * extraMult;
            var newItems = new List<FeedItem>();

            foreach (var p in _roster)
            {
                if (!_calendar.CanTrain(p.Grade, week)) continue;
                var stage = _calendar.StageIndex(p.Grade, week);
                var before = Snapshot(p);
                DevelopmentModel.TrainWeek(p, menu, stage, campMult, _stages, _training);
                foreach (var k in AbilityKinds.All)
                {
                    var gained = p.Level(k) - before[k];
                    if (gained > 0)
                    {
                        newItems.Add(new FeedItem
                        {
                            When = "今週",
                            Text = p.Name + " の " + AbilityJp(k) + " が +" + gained,
                            Kind = FeedKind.Up,
                            Tag = "成長",
                        });
                    }
                }
            }

            PushFeed(newItems);
        }

        // ===== 大会モード（要件1〜7） =====

        public bool InTournament => GameSession.Current.InTournament;
        public bool BannerPending => GameSession.Current.BannerPending;
        public bool ReachedMatchDay => GameSession.Current.ReachedMatchDay;
        public bool TournamentFinished => GameSession.Current.Runner?.Finished ?? false;
        public PlayerMatchOutcome LastOutcome => GameSession.Current.LastOutcome;

        /// <summary>開幕バナー上段（例「2028年　夏」）。</summary>
        public string BannerTop
        {
            get
            {
                var year = SeasonBaseDisplayYear();
                return year + "年　" + (GameSession.Current.Kind == TournamentKind.Summer ? "夏" : "秋");
            }
        }
        /// <summary>開幕バナー中段（大会名）。</summary>
        public string BannerName => GameSession.Current.Title;

        /// <summary>次戦ダイアログ用ラウンド名（例「２回戦」）。</summary>
        public string NextRoundLabel => GameSession.Current.Runner?.RoundName ?? "";

        /// <summary>次戦ダイアログ用の対戦表記（例「vs 享栄 [B]」）。</summary>
        public string NextVsLabel
        {
            get
            {
                var r = GameSession.Current.Runner;
                if (r == null || r.NextOpponent == null) return "";
                return "vs " + r.NextOpponent.Name + " [" + r.NextOpponentTier + "]";
            }
        }

        /// <summary>大会モードで1日進める。7日跨ぎで効果低下の練習1週を適用する（要件3・6）。</summary>
        public void AdvanceDay()
        {
            var practiceDue = GameSession.Current.AdvanceDay();
            if (practiceDue) RunPracticeWeek(_training.TournamentPracticeMult);
        }

        /// <summary>自校の次戦を自動消化する（要件7「はい」）。結果をフィード化する。</summary>
        public PlayerMatchOutcome PlayMatch()
        {
            var o = GameSession.Current.PlayMatch();
            var verb = o.ManagerWon ? "○ 勝利" : "● 敗戦";
            PushFeed(new List<FeedItem>
            {
                new FeedItem
                {
                    When = "本日",
                    Text = o.RoundName + " vs " + o.OpponentName + "　" + o.ManagerScore + "-" + o.OpponentScore + "　" + verb,
                    Kind = o.ManagerWon ? FeedKind.Up : FeedKind.Warn,
                    Tag = o.ManagerWon ? "情報" : "警告",
                },
            });
            return o;
        }

        /// <summary>自校の次戦をライブ観戦で始める（試合開始フロー・観戦画面へ渡す進行体を得る）。</summary>
        public LivePlayerMatch BeginMatch() => GameSession.Current.BeginMatch();

        /// <summary>ライブ観戦した自校戦の終局結果を大会へ反映し、結果をフィード化する（PlayMatch と同じ通知）。</summary>
        public PlayerMatchOutcome CompleteMatch(KokoSim.Engine.Match.Game.GameResult result)
        {
            var o = GameSession.Current.CompleteMatch(result);
            var verb = o.ManagerWon ? "○ 勝利" : "● 敗戦";
            PushFeed(new List<FeedItem>
            {
                new FeedItem
                {
                    When = "本日",
                    Text = o.RoundName + " vs " + o.OpponentName + "　" + o.ManagerScore + "-" + o.OpponentScore + "　" + verb,
                    Kind = o.ManagerWon ? FeedKind.Up : FeedKind.Warn,
                    Tag = o.ManagerWon ? "情報" : "警告",
                },
            });
            return o;
        }

        /// <summary>結果表示を閉じる。大会が終了していれば通常モードへ戻す（要件）。</summary>
        public void DismissResult()
        {
            GameSession.Current.ConsumeResult();
            var r = GameSession.Current.Runner;
            if (r != null && r.Finished)
            {
                var summary = r.IsChampion ? GameSession.Current.Title + " 優勝！" : GameSession.Current.Title + " 敗退";
                PushFeed(new List<FeedItem>
                {
                    new FeedItem { When = "大会", Text = summary, Kind = r.IsChampion ? FeedKind.Up : FeedKind.Normal, Tag = "情報" },
                });
                // 大会が消費した週ぶん共有週を進めてから通常モードへ戻す。
                var weeks = System.Math.Max(1, GameSession.Current.TournamentDay / 7);
                KokoSim.Unity.Shell.GameClock.Advance(weeks);
                GameSession.Current.ExitTournament();
            }
        }

        public void ConsumeBanner() => GameSession.Current.ConsumeBanner();

        private void EnterTournament(TournamentKind kind)
        {
            var manager = BuildManagerSchool();
            var field = BuildField(manager);
            // 大会シードは母種（プレイ毎に変動）＋年・種別で導出。これで「毎回まったく同じ試合」を解消しつつ、
            // 同じ母種なら完全再現される（決定論は維持）。年・種別を混ぜて同一ゲーム内の各大会も別展開にする。
            var seed = KokoSim.Unity.Shell.GameSeed.Master
                       ^ (ulong)(9000 + KokoSim.Unity.Shell.GameClock.YearIndex * 10 + (int)kind);
            // 自校の一戦だけ詳細試合エンジンで解決（成績が実データで積まれる）。裏試合は従来の抽象シムのまま。
            var runner = new TournamentRunner(field, manager, NationCoeff, new Xoshiro256Random(seed), Schedule,
                TournamentTitle(kind), new PlayerMatchResolver());
            // field も渡す（大会展望が実際の出場校＝自校＋県内校を引くため）。
            GameSession.Current.EnterTournament(kind, TournamentTitle(kind), runner, field);
            GameSession.Current.Year = KokoSim.Unity.Shell.GameClock.YearIndex;

            PushFeed(new List<FeedItem>
            {
                new FeedItem { When = "本日", Text = TournamentTitle(kind) + " が開幕した。", Kind = FeedKind.Discover, Tag = "情報" },
            });
        }

        private School BuildManagerSchool()
        {
            var trainable = _roster.Where(p => p.Grade <= 3).ToList();
            // 総合＝6指標のリーグ標準化総合（③）。旧 Average(AverageLevel) から統一。
            var strength = trainable.Count == 0 ? 40.0 : KokoSim.Unity.Shell.TeamOverall.Of(trainable);
            return new School { Id = ManagerSchoolId, Name = "桜丘", PrefectureId = FieldPrefectureId, Strength = strength };
        }

        private static List<School> BuildField(School manager)
        {
            if (_nation == null)
                _nation = NationGenerator.Generate(
                    KokoSim.Unity.Shell.SchoolNameVocabProvider.Default, NationCoeff, new Xoshiro256Random(2026));

            var field = new List<School> { manager };
            foreach (var s in _nation.InPrefecture(FieldPrefectureId))
            {
                if (s.Id == manager.Id) continue;
                field.Add(s);
            }
            return field;
        }

        private static string TournamentTitle(TournamentKind kind)
        {
            var year = SeasonBaseDisplayYear();
            return kind == TournamentKind.Summer
                ? year + "年 選手権神奈川大会"
                : year + "年 秋季神奈川県大会";
        }

        private static int SeasonBaseDisplayYear()
            => KokoSim.Unity.Shell.SeasonClock.SeasonBaseYear + (KokoSim.Unity.Shell.GameClock.YearIndex - 1);

        private void PushFeed(List<FeedItem> newItems)
        {
            foreach (var it in newItems.Take(6)) _feed.Insert(0, it);
            while (_feed.Count > 12) _feed.RemoveAt(_feed.Count - 1);
        }

        /// <summary>次の試合カード＋カウントダウンを大会仕様で埋める（要件4・5）。</summary>
        private void FillTournamentView(HomeView view, int week)
        {
            var s = GameSession.Current;
            var r = s.Runner;
            view.TournamentMode = true;
            view.CountdownLabel = "次戦まで";

            var kindWord = s.Kind == TournamentKind.Summer ? "夏" : "秋";
            view.WeekLabel += "　" + kindWord + "の大会 " + (s.TournamentDay + 1) + "日目";

            view.HomeTeam = "桜丘";
            view.HomeSub = "自校";
            view.Venue = s.Title;
            view.Weather = "―";

            if (r.Finished)
            {
                view.CountdownValue = "―";
                view.GameTag = r.IsChampion ? "優勝" : "敗退";
                view.AwayTeam = "―";
                view.AwaySub = "";
                view.GameDate = r.IsChampion ? "全試合終了" : "大会敗退";
            }
            else
            {
                var days = s.DaysUntilNextMatch;
                var reached = s.ReachedMatchDay;
                view.CountdownValue = reached ? "本日 試合" : days + " 日";
                view.GameTag = r.RoundName;
                view.AwayTeam = r.NextOpponent?.Name ?? "―";
                view.AwaySub = r.NextOpponent != null ? "ランク " + r.NextOpponentTier : "";
                view.GameDate = reached ? "本日 試合" : "試合まで " + days + " 日";
            }
        }

        public HomeView BuildView()
        {
            var _week = KokoSim.Unity.Shell.GameClock.Week;        // 共有現在週
            var _year = KokoSim.Unity.Shell.GameClock.YearIndex;   // 共有年度
            var view = new HomeView();
            view.WeekLabel = KokoSim.Unity.Shell.SeasonClock.CurrentLabel(_year, _week);   // 共通「YYYY年M月W週目」
            view.Funds = "¥120万";
            view.FameGrade = "D";
            view.TrustGrade = "C";

            var trainable = _roster.Where(p => p.Grade <= 3).ToList();
            view.TeamRankGrade = KokoSim.Unity.Shell.TeamOverall.GradeOf(trainable);

            // 部の状態。
            view.Injuries = 0;
            view.InjuredNames = "";

            if (GameSession.Current.InTournament)
            {
                FillTournamentView(view, _week);
            }
            else
            {
                // 通常モード: 夏予選までの残り週（設計書03: 夏は第15週前後）。次戦は練習試合の仮表示。
                view.TournamentMode = false;
                view.CountdownLabel = "夏予選まで";
                var left = _calendar.SummerTournamentStartWeek - _week;
                view.CountdownValue = left > 0 ? "残り " + left + " 週" : "開催中";

                view.GameTag = "練習試合";
                view.GameDate = "第" + (_week + 2) + "週（土）";
                view.Venue = "自校グラウンド";
                view.Weather = "曇";
            }

            // 練習計画（メニュー巡回を7日で表現。土曜は練習試合）。
            for (var d = 0; d < 7; d++)
            {
                var isMatch = d == 5; // 土
                var m = WeekMenus[(_week + d) % WeekMenus.Length];
                view.Plan.Add(new PlanDay
                {
                    Day = DayNames[d],
                    Menu = isMatch ? "練習試合 vs 北都大付属" : MenuJp(m),
                    Match = isMatch,
                });
            }

            // 注目選手（中核平均が高い順に4名）。
            foreach (var p in _roster.OrderByDescending(x => x.AverageLevel()).Take(4))
            {
                view.Roster.Add(BuildRow(p));
            }

            // 個別指導（設計書04。枠のみ＝上位2名を仮表示＋空き1。効果はエンジン未接続 Q7）。
            var top = _roster.OrderByDescending(x => x.AverageLevel()).Take(2).ToList();
            view.Guidance.Add(new GuidanceSlot { Name = top.Count > 0 ? top[0].Name : "", Focus = "打撃強化", Empty = top.Count == 0 });
            view.Guidance.Add(new GuidanceSlot { Name = top.Count > 1 ? top[1].Name : "", Focus = "投球", Empty = top.Count <= 1 });
            view.Guidance.Add(new GuidanceSlot { Empty = true });
            view.GuidanceUsed = view.Guidance.Count(g => !g.Empty);

            view.Feed = _feed.ToList();
            return view;
        }

        private static RosterRow BuildRow(DevelopingPlayer p)
        {
            var overall = (int)p.AverageLevel();
            return new RosterRow
            {
                Name = p.Name,
                Position = p.IsPitcher ? "投手" : "野手",
                OverallGrade = Tiers.FromStrength(overall).ToString(),
                Condition = ConditionJp(p.ConditionValue),
            };
        }

        // 調子（内部連続値→5段階）を日本語表示（設計書02 §3.3。正ソースは FormModel）。
        private static string ConditionJp(double conditionValue)
        {
            switch (KokoSim.Engine.Players.FormModel.Quantize(conditionValue))
            {
                case KokoSim.Engine.Players.Condition.Excellent: return "絶好調";
                case KokoSim.Engine.Players.Condition.Good: return "好調";
                case KokoSim.Engine.Players.Condition.Poor: return "不調";
                case KokoSim.Engine.Players.Condition.Terrible: return "絶不調";
                default: return "普通";
            }
        }

        private static Dictionary<AbilityKind, int> Snapshot(DevelopingPlayer p)
        {
            var d = new Dictionary<AbilityKind, int>();
            foreach (var k in AbilityKinds.All) d[k] = p.Level(k);
            return d;
        }

        private static string AbilityJp(AbilityKind k)
        {
            switch (k)
            {
                case AbilityKind.Contact: return "ミート";
                case AbilityKind.Power: return "パワー";
                case AbilityKind.LaunchTendency: return "弾道";
                case AbilityKind.Discipline: return "選球眼";
                case AbilityKind.Speed: return "走力";
                case AbilityKind.ArmStrength: return "肩";
                case AbilityKind.Fielding: return "守備";
                case AbilityKind.Catching: return "捕球";
                case AbilityKind.Velocity: return "球速";
                case AbilityKind.Control: return "制球";
                case AbilityKind.Stamina: return "スタミナ";
                case AbilityKind.PitchRank: return "球種";
                default: return k.ToString();
            }
        }

        private static string MenuJp(TrainingMenu m)
        {
            switch (m)
            {
                case TrainingMenu.Batting: return "打撃";
                case TrainingMenu.Strength: return "筋力";
                case TrainingMenu.BaseRunning: return "走塁";
                case TrainingMenu.Defense: return "守備";
                case TrainingMenu.Throwing: return "遠投";
                case TrainingMenu.Pitching: return "投げ込み";
                case TrainingMenu.BreakingBall: return "変化球";
                case TrainingMenu.Running: return "走り込み";
                default: return "休養";
            }
        }
    }
}

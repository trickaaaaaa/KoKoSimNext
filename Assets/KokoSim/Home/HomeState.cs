// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
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

    /// <summary>故障者1名ぶんの表示行（設計書03 §3.5: 症状は常に可視）。</summary>
    public sealed class InjuredRow
    {
        public string Number = "—";
        public string Name = "";
        public string Site = "";       // 肩 / 肘 / 腰 / 膝 / 足首 / 手
        public string Type = "";       // 捻挫 / 肉離れ / 骨折 / 打撲 / 靭帯損傷 / 疲労性炎症（種類なしは空）
        public string Severity = "";   // 軽度 / 中度 / 重度
        public string Back = "";       // 復帰まで（例「あと 4 週」）
        public bool Severe;            // 重度＝警告色（UI原則②: 本当の警告だけ）
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
        /// <summary>掲示板の升目に載せる短縮カウントダウン（"15週" / "3日" / "本日試合" / "開催中" / "―"）。</summary>
        public string CountdownCells = "";
        /// <summary>年の小書きに続けて出す補足（"夏の大会 3日目"）。升目には載せない。</summary>
        public string WeekSuffix = "";
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

        public bool TournamentMode;   // 大会モード中は次戦カード/カウントダウンを大会仕様に切替える

        // 主役ヒーロー帯・通常週のカウントダウン（数字は Oswald にするため単位と分割して持つ）。
        public string HeroBigValue = "";   // "15"（週数）／数字が無い状態は "開催中"
        public string HeroBigUnit = "";    // "週"／数字が無い状態は空

        public int GuidanceUsed;
        public int GuidanceTotal = 3;

        public List<InjuredRow> Injured = new List<InjuredRow>();
        public List<FeedItem> Feed = new List<FeedItem>();
        public List<GuidanceSlot> Guidance = new List<GuidanceSlot>();
    }

    /// <summary>
    /// ホーム画面の状態機械。純エンジンを駆動して「今週を進める」を実体化する。
    /// 週送りで練習→育成・怪我・イベントを回し、結果をフィードに流す（設計書06 §2: 通知フィード駆動）。
    ///
    /// 部員は全画面共有の <see cref="RosterService"/> を単一ソースにする（2026-07-21）。
    /// 以前はここだけ seed=42 で私物のロスターを再生成しており、週送りの成長が共有ロスターへ届かず
    /// 画面往復で消えていた（引退は GameClock 経由で共有側に起きるため、育てた選手と引退する選手が別物だった）。
    /// あわせて状態自体もセッション常駐（<see cref="Current"/>）にし、通知フィードが画面往復で消えないようにする。
    /// </summary>
    public sealed class HomeState
    {
        private readonly IRandomSource _rng;
        private readonly SeasonCalendar _calendar = new SeasonCalendar();
        private readonly GrowthStageTable _stages = new GrowthStageTable();
        private readonly TrainingCoefficients _training = new TrainingCoefficients();
        private readonly InjuryCoefficients _injury = new InjuryCoefficients();
        private readonly SkillCoefficients _skills = new SkillCoefficients();
        private readonly List<FeedItem> _feed = new List<FeedItem>();

        // 現在週/年度は全画面共有の GameClock、大会モードの進行は GameSession を単一ソースとする（画面ごとに持たない）。
        // 大会モードの構築（進行体・裏試合フルシム）は TournamentEntry（Shell）へ移した（issue #134）。

        private static HomeState _current;

        /// <summary>セッション常駐の実体（通知フィードが画面往復で消えないようにする）。</summary>
        public static HomeState Current => _current ?? (_current = new HomeState());

        /// <summary>週送りの対象＝全画面共有の在籍部員（引退者は含まない）。</summary>
        private static IReadOnlyList<DevelopingPlayer> Roster => RosterService.Active;

        public HomeState(ulong seed = 42)
        {
            _rng = new Xoshiro256Random(seed);
            _feed.Add(new FeedItem { When = "先週", Text = "新チームが始動した。", Kind = FeedKind.Normal, Tag = "情報" });
        }

        /// <summary>今週を進める: 全部員を1週練習させ→怪我の週次処理→共有週を進める。
        /// 大会開幕週の大会モード遷移は共通クロック <see cref="GameClock"/> の週入りフックへ集約した
        /// （issue #134: どのタブから進めても同じ地点で判定する）。開幕バナーは <see cref="BannerPending"/> を
        /// 見てホームが出す。</summary>
        public void AdvanceWeek()
        {
            RunPracticeWeek(1.0);                       // 通常週の練習（効果1.0）
            RunInjuryWeek();                            // 怪我の発生判定と回復（設計書03 §3.5）
            KokoSim.Unity.Shell.GameClock.Advance();    // 共有週を進める（大会入り判定は EnterWeek に集約）
        }

        /// <summary>開幕演出（バナー）を出す直前に「大会が開幕した」を通知フィードへ流す（設計書06 §2）。
        /// 大会モード遷移自体は <see cref="TournamentEntry"/> が行うため、フィード投入だけをここに残す。</summary>
        public void PushTournamentOpenFeed()
        {
            PushFeed(new List<FeedItem>
            {
                new FeedItem { When = "本日", Text = GameSession.Current.Title + " が開幕した。", Kind = FeedKind.Discover, Tag = "情報" },
            });
        }

        /// <summary>1週ぶんの練習を適用する。extraMult は大会モード時の効果低下（&lt;1.0）に使う。</summary>
        private void RunPracticeWeek(double extraMult)
        {
            var week = KokoSim.Unity.Shell.GameClock.Week;
            var menu = WeekMenus[week % WeekMenus.Length];
            var campMult = _calendar.CampMultiplier(week, _training) * extraMult;
            var newItems = new List<FeedItem>();

            foreach (var p in Roster)
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

        /// <summary>
        /// 怪我の週次処理（設計書03 §3.5: 発生判定→回復進行）。SeasonEngine と同じ順序・同じ独立ストリームで回す。
        /// 発生・復帰はフィードへ流す（通知フィード駆動）。怪我中の選手は DevelopmentModel 側で練習が止まる。
        /// </summary>
        private void RunInjuryWeek()
        {
            var week = KokoSim.Unity.Shell.GameClock.Week;
            var year = KokoSim.Unity.Shell.GameClock.YearIndex;
            var rng = _rng.Fork(0x1213_0000UL ^ (ulong)(year * 100 + week));
            var newItems = new List<FeedItem>();

            foreach (var p in Roster)
            {
                if (p.Injury == InjurySeverity.None)
                {
                    if (!InjuryModel.WeeklyCheck(p, rng, _injury, _skills, InjuryCatalog.Default)) continue;
                    newItems.Add(new FeedItem
                    {
                        When = "今週",
                        Text = p.Name + " が " + InjuryLabel.Diagnosis(p.InjuryType, p.InjurySite) + "（"
                               + InjuryLabel.Severity(p.Injury) + "・全治 "
                               + InjuryLabel.WeeksToFullRecovery(p) + " 週）",
                        Kind = FeedKind.Warn,
                        Tag = "警告",
                    });
                }
                else
                {
                    InjuryModel.WeeklyRecover(p, _injury, InjuryCatalog.Default);
                    if (p.Injury != InjurySeverity.None) continue;
                    newItems.Add(new FeedItem
                    {
                        When = "今週",
                        Text = p.Name + " が怪我から復帰した。",
                        Kind = FeedKind.Up,
                        Tag = "情報",
                    });
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
            if (!practiceDue) return;
            RunPracticeWeek(_training.TournamentPracticeMult);
            RunInjuryWeek();
        }

        /// <summary>自校の次戦を自動消化する（要件7「はい」）。結果をフィード化する。</summary>
        public PlayerMatchOutcome PlayMatch()
        {
            var o = GameSession.Current.PlayMatch();
            PushMatchFeed(o);
            return o;
        }

        /// <summary>自校の次戦をライブ観戦で始める（試合開始フロー・観戦画面へ渡す進行体を得る）。</summary>
        public LivePlayerMatch BeginMatch() => GameSession.Current.BeginMatch();

        /// <summary>ライブ観戦した自校戦の終局結果を大会へ反映し、結果をフィード化する（PlayMatch と同じ通知）。</summary>
        public PlayerMatchOutcome CompleteMatch(KokoSim.Engine.Match.Game.GameResult result)
        {
            var o = GameSession.Current.CompleteMatch(result);
            PushMatchFeed(o);
            return o;
        }

        private void PushMatchFeed(PlayerMatchOutcome o)
        {
            var verb = o.ManagerWon ? "○ 勝利" : "● 敗戦";
            var items = new List<FeedItem>
            {
                new FeedItem
                {
                    When = "本日",
                    Text = o.RoundName + " vs " + o.OpponentName + "　" + o.ManagerScore + "-" + o.OpponentScore + "　" + verb,
                    Kind = o.ManagerWon ? FeedKind.Up : FeedKind.Warn,
                    Tag = o.ManagerWon ? "情報" : "警告",
                },
            };
            items.AddRange(InjuryFeedItems());
            PushFeed(items);
        }

        /// <summary>
        /// 試合中の受傷・押して出場の結果を通知フィードへ流す（設計書03 §3.5・設計書06 §3.1）。
        /// 文言はエンジンの結果（傷病名・部位・段階・全治週）をそのまま整形するだけ。
        /// </summary>
        private static List<FeedItem> InjuryFeedItems()
        {
            var items = new List<FeedItem>();
            foreach (var n in GameSession.Current.DrainInjuryNotices())
            {
                string text;
                switch (n.Kind)
                {
                    case MatchInjuryOutcomeKind.Occurred:
                        text = n.PlayerName + " が試合中に " + InjuryLabel.Diagnosis(n.Type, n.Site) + "（"
                               + InjuryLabel.Severity(n.Severity) + "・全治 " + n.WeeksRemaining + " 週）";
                        break;
                    case MatchInjuryOutcomeKind.Worsened:
                        text = n.PlayerName + " が押して出場し悪化（" + InjuryLabel.Severity(n.Severity)
                               + "・あと " + n.WeeksRemaining + " 週）";
                        break;
                    default:
                        text = n.PlayerName + " は怪我を押して出場した（復帰が " + n.WeeksRemaining + " 週先へ）";
                        break;
                }
                items.Add(new FeedItem { When = "本日", Text = text, Kind = FeedKind.Warn, Tag = "警告" });
            }
            return items;
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

        private static int SeasonBaseDisplayYear()
            => KokoSim.Unity.Shell.SeasonClock.SeasonBaseYear + (KokoSim.Unity.Shell.GameClock.YearIndex - 1);

        private void PushFeed(List<FeedItem> newItems)
        {
            foreach (var it in newItems.Take(6)) _feed.Insert(0, it);
            while (_feed.Count > 12) _feed.RemoveAt(_feed.Count - 1);
        }

        /// <summary>次の試合カード＋カウントダウンを大会仕様で埋める（要件4・5）。</summary>
        private void FillTournamentView(HomeView view)
        {
            var s = GameSession.Current;
            var r = s.Runner;
            view.TournamentMode = true;
            view.CountdownLabel = "次戦まで";

            var kindWord = s.Kind == TournamentKind.Summer ? "夏" : "秋";
            view.WeekSuffix = kindWord + "の大会 " + (s.TournamentDay + 1) + "日目";

            view.HomeTeam = "桜丘";
            view.HomeSub = "自校";
            view.Venue = s.Title;
            view.Weather = "―";

            if (r.Finished)
            {
                view.CountdownValue = "―";
                view.CountdownCells = "―";
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
                view.CountdownCells = reached ? "本日試合" : days + "日";
                view.GameTag = r.RoundName;
                view.AwayTeam = r.NextOpponent?.Name ?? "―";
                view.AwaySub = r.NextOpponent != null ? "ランク " + r.NextOpponentTier : "";
                view.GameDate = reached ? "本日 試合" : "試合まで " + days + " 日";
            }
        }

        public HomeView BuildView()
        {
            var week = KokoSim.Unity.Shell.GameClock.Week;        // 共有現在週
            var year = KokoSim.Unity.Shell.GameClock.YearIndex;   // 共有年度
            var view = new HomeView();
            view.WeekLabel = KokoSim.Unity.Shell.SeasonClock.CurrentLabel(year, week);   // 共通「YYYY年M月W週目」
            // 資金は監督メタ（ManagerService）が単一ソース。練習試合の費用減算がそのまま反映される。
            view.Funds = "¥" + KokoSim.Unity.Shell.ManagerService.Manager.Funds.ToString("0") + "万";
            view.FameGrade = "D";
            view.TrustGrade = "C";

            var roster = Roster;
            view.TeamRankGrade = KokoSim.Unity.Shell.TeamOverall.GradeOf(roster);

            // 故障者（設計書03 §3.5: 部位・程度は常に可視）。重い順→学年順で並べる。
            foreach (var p in roster.Where(x => x.Injury != InjurySeverity.None)
                         .OrderByDescending(x => (int)x.Injury).ThenBy(x => x.Id))
            {
                view.Injured.Add(new InjuredRow
                {
                    Number = p.UniformNumber == 0 ? "—" : p.UniformNumber.ToString(),
                    Name = p.Name,
                    Site = InjuryLabel.Site(p.InjurySite),
                    Type = InjuryLabel.Type(p.InjuryType),
                    Severity = InjuryLabel.Severity(p.Injury),
                    Back = "あと " + InjuryLabel.WeeksToFullRecovery(p) + " 週",
                    Severe = p.Injury == InjurySeverity.Severe,
                });
            }

            if (GameSession.Current.InTournament)
            {
                FillTournamentView(view);
            }
            else
            {
                // 通常モード: 夏予選までの残り週（設計書03: 夏は第15週前後）。
                // 「次の試合」カードは大会モード中だけ出す（通常週はダミーの対戦カードを出さない）。
                view.TournamentMode = false;
                view.CountdownLabel = "夏予選まで";
                var left = _calendar.SummerTournamentStartWeek - week;
                view.CountdownValue = left > 0 ? "残り " + left + " 週" : "開催中";
                view.CountdownCells = left > 0 ? left + "週" : "開催中";
                // 主役ヒーロー帯（通常週）: 数字＝Oswald にするため単位と分けて持つ。
                view.HeroBigValue = left > 0 ? left.ToString() : "開催中";
                view.HeroBigUnit = left > 0 ? "週" : "";
            }

            // 個別指導（設計書04。枠のみ＝上位2名を仮表示＋空き1。効果はエンジン未接続 Q7）。
            var top = roster.OrderByDescending(x => x.AverageLevel()).Take(2).ToList();
            view.Guidance.Add(new GuidanceSlot { Name = top.Count > 0 ? top[0].Name : "", Focus = "打撃強化", Empty = top.Count == 0 });
            view.Guidance.Add(new GuidanceSlot { Name = top.Count > 1 ? top[1].Name : "", Focus = "投球", Empty = top.Count <= 1 });
            view.Guidance.Add(new GuidanceSlot { Empty = true });
            view.GuidanceUsed = view.Guidance.Count(g => !g.Empty);

            view.Feed = _feed.ToList();
            return view;
        }

        // ===== 表示用の変換 =====

        private static readonly TrainingMenu[] WeekMenus =
        {
            TrainingMenu.Batting, TrainingMenu.Defense, TrainingMenu.Pitching,
            TrainingMenu.Strength, TrainingMenu.BaseRunning, TrainingMenu.Running, TrainingMenu.Rest,
        };

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
    }
}

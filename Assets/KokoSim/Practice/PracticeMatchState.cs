using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Practice;
using KokoSim.Unity.Players;   // AbilityBar / RadarAxis を共用
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Practice
{
    /// <summary>申込先一覧の1行（データ濃密テーブル・行高32px）。</summary>
    public sealed class PracticeOpponentRow
    {
        public int SchoolId;
        public string Name = "";
        public string TierLetter = "D";
        public int Overall;
        public string Tradition = "";
        public int AcceptPercent;
    }

    /// <summary>選択中の相手校の詳細（6角形＋指標＋申込条件）。</summary>
    public sealed class PracticeOpponentDetail
    {
        public string Name = "";
        public string TierLetter = "D";
        public int Overall;
        public int AcceptPercent;
        public List<AbilityBar> Factors = new List<AbilityBar>();
        public List<RadarAxis> Radar = new List<RadarAxis>();
    }

    /// <summary>練習試合画面に表示する一式（スナップショット）。</summary>
    public sealed class PracticeMatchView
    {
        public string WeekLabel = "";
        public string OwnLabel = "";              // 自校名（総合 X / 値）
        public string FundsText = "";
        public string CostText = "";
        public List<PracticeOpponentRow> Opponents = new List<PracticeOpponentRow>();
        public PracticeOpponentDetail Selected;   // null=未選択
        public bool ActionEnabled;
        public string ActionLabel = "練習試合を申し込む";
        public string StatusText = "";            // 申込条件・直近結果のメッセージ
        public bool StatusIsWarning;
    }

    /// <summary>
    /// 練習試合画面の状態（設計書03 §週ターン③ 週末アクション・設計書04 §名声）。
    /// 申込先は同県内の全高校。相手の6指標は自校と同じ <see cref="TeamStrengthProfile"/> を通して出すので
    /// レーダーの意味とスケールが自校のチーム総合力パネルと一致する。
    /// 週1制約・資金・受諾判定はエンジン（<see cref="PracticeMatchScheduler"/>）が持ち、ここは表示と入力の中継だけ。
    /// UnityEngine 非依存（設計書06 §1）。
    /// </summary>
    public sealed class PracticeMatchState
    {
        private static readonly TeamStrengthCoefficients Coeff = new TeamStrengthCoefficients();

        // 相手校の6指標は「校ID＋年度」で決まる決定論なので、画面を開き直しても同じ値になる。都度の再集計は重いためキャッシュする。
        private static readonly Dictionary<int, TeamStrength> ProfileCache = new Dictionary<int, TeamStrength>();

        private int _selectedId = int.MinValue;
        private string _message = "";
        private bool _messageIsWarning;

        /// <summary>行クリックで相手を選ぶ。</summary>
        public void Select(int schoolId)
        {
            _selectedId = schoolId;
            _message = "";
            _messageIsWarning = false;
        }

        public PracticeMatchView BuildView()
        {
            var manager = ManagerService.Manager;
            var own = NationService.ManagerSchool();
            var scheduler = ManagerService.Practice;

            var v = new PracticeMatchView
            {
                WeekLabel = GameClock.CurrentLabel(),
                OwnLabel = own.Name + "　総合 " + own.Tier + "（" + (int)Math.Round(own.Strength) + "）",
                FundsText = "¥" + manager.Funds.ToString("0") + "万",
                CostText = "¥" + scheduler.Cost.ToString("0") + "万",
            };

            foreach (var s in NationService.PrefectureSchools)
            {
                v.Opponents.Add(new PracticeOpponentRow
                {
                    SchoolId = s.Id,
                    Name = s.Name,
                    TierLetter = s.Tier.ToString(),
                    Overall = (int)Math.Round(s.Strength),
                    Tradition = TraditionLabel(s.Tradition),
                    AcceptPercent = Percent(scheduler.AcceptChance(manager, own, s)),
                });
            }

            var selected = Find(_selectedId);
            if (selected != null) v.Selected = BuildDetail(selected, own);

            // 申込条件（エンジンの判定をそのまま文言化する）。
            var block = scheduler.CanRequest(manager, ManagerService.AbsoluteWeek);
            var inTournament = GameSession.Current.InTournament;

            v.ActionEnabled = selected != null && block == PracticeMatchRejection.None && !inTournament;
            v.ActionLabel = selected == null
                ? "相手校を選んでください"
                : "練習試合を申し込む（" + v.CostText + "）";

            if (!string.IsNullOrEmpty(_message))
            {
                v.StatusText = _message;
                v.StatusIsWarning = _messageIsWarning;
            }
            else if (inTournament)
            {
                v.StatusText = "大会期間中は練習試合を組めません。";
                v.StatusIsWarning = true;
            }
            else if (block == PracticeMatchRejection.AlreadyPlayedThisWeek)
            {
                v.StatusText = "今週の練習試合は消化済みです（週1回まで）。";
                v.StatusIsWarning = true;
            }
            else if (block == PracticeMatchRejection.InsufficientFunds)
            {
                v.StatusText = "資金が足りません（必要 " + v.CostText + "）。";
                v.StatusIsWarning = true;
            }
            else
            {
                v.StatusText = "週1回まで。成績は通算に加算され、公式戦通算には残りません。";
            }

            return v;
        }

        /// <summary>選択中の相手へ申し込み、成立すれば試合を消化する。結果はメッセージに残す。</summary>
        public void Request()
        {
            var opponent = Find(_selectedId);
            if (opponent == null) return;

            var own = NationService.ManagerSchool();
            var week = ManagerService.AbsoluteWeek;
            // シードは「相手校ID＋絶対週」から導く決定論（不変条件#2）。同じ週に同じ相手なら同じ試合になる。
            var rng = new Xoshiro256Random(0x9E37_79B9UL ^ (ulong)(long)opponent.Id ^ ((ulong)(long)week * 0x85EB_CA6BUL));

            var outcome = GameSession.Current.PlayPracticeMatch(own, opponent, rng);
            _messageIsWarning = !outcome.Played;
            switch (outcome.Rejection)
            {
                case PracticeMatchRejection.AlreadyPlayedThisWeek:
                    _message = "今週の練習試合は消化済みです（週1回まで）。";
                    break;
                case PracticeMatchRejection.InsufficientFunds:
                    _message = "資金が足りません。";
                    break;
                case PracticeMatchRejection.Declined:
                    _message = opponent.Name + " に申込を断られた（受諾見込み "
                               + Percent(outcome.AcceptChance) + "％）。名声を上げれば格上とも組める。";
                    break;
                default:
                    _message = ScoreLine(opponent.Name, outcome.Detail.Result);
                    break;
            }
        }

        private static string ScoreLine(string opponentName, GameResult r)
        {
            // 自校＝後攻(home)固定（PlayerMatchResolver）。
            var mine = r.HomeRuns;
            var theirs = r.AwayRuns;
            var mark = mine > theirs ? "○" : mine < theirs ? "●" : "△";
            return "練習試合 " + mark + "　vs " + opponentName + "　" + mine + " - " + theirs
                   + "　（出場者の精神力・走塁判断・捕手リードが伸びた）";
        }

        private static PracticeOpponentDetail BuildDetail(School s, School own)
        {
            var strength = Profile(s);
            var d = new PracticeOpponentDetail
            {
                Name = s.Name,
                Overall = (int)Math.Round(strength.Overall),
                TierLetter = Tiers.FromStrength(strength.Overall).ToString(),
                AcceptPercent = Percent(ManagerService.Practice.AcceptChance(ManagerService.Manager, own, s)),
            };

            // 軸順はチーム総合力パネルと同一（並べて比較できる）。
            Add(d, "打撃力", strength.Batting);
            Add(d, "投手力", strength.Pitching);
            Add(d, "守備力", strength.Defense);
            Add(d, "機動力", strength.Mobility);
            Add(d, "選手層", strength.Depth);
            Add(d, "精神力", strength.Mental);
            return d;
        }

        private static TeamStrength Profile(School s)
        {
            if (ProfileCache.TryGetValue(s.Id, out var cached)) return cached;
            var team = StrengthTeamFactory.ForSchool(s, GameSession.Current.Year);
            var strength = ScoutedTeamProfile.Compute(team, Coeff);
            ProfileCache[s.Id] = strength;
            return strength;
        }

        private static void Add(PracticeOpponentDetail d, string label, double value)
        {
            var pct = (float)Math.Max(0.0, Math.Min(1.0, value / 100.0));
            var grade = Tiers.FromStrength(value).ToString();
            d.Factors.Add(new AbilityBar
            {
                Label = label,
                Value = (int)Math.Round(value),
                Grade = grade,
                Pct = pct,
            });
            d.Radar.Add(new RadarAxis
            {
                Label = label,
                Value01 = pct,
                ValueText = ((int)Math.Round(value)).ToString(),
            });
        }

        private static School Find(int schoolId)
        {
            foreach (var s in NationService.PrefectureSchools)
                if (s.Id == schoolId) return s;
            return null;
        }

        private static int Percent(double p) => (int)Math.Round(p * 100.0);

        private static string TraditionLabel(Tradition t)
        {
            switch (t)
            {
                case Tradition.Storied: return "名門";
                case Tradition.Midlevel: return "中堅";
                default: return "新興";
            }
        }
    }
}

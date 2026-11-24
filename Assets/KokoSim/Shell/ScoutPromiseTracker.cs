using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 入部の条件（約束）の決着を反映する合流点（設計書04 §2.4）。公式戦・年度替わり・背番号決定・主将指名・入部の
    /// 節目で engine の <see cref="ScoutPromiseJudge"/> を呼び、決着した約束ぶんの信頼度と名声を動かす。
    /// 破った＝信頼度と名声が下がる（1回だけ）／守り切った＝信頼度が少し上がる。通知はホームが引き取る
    /// （遷移とUI提示の分離）。
    /// </summary>
    public static class ScoutPromiseTracker
    {
        private static readonly ScoutCoefficients Coeff = new ScoutCoefficients();
        private static readonly List<ScoutPromiseOutcome> _notices = new List<ScoutPromiseOutcome>();

        /// <summary>自校の公式戦1試合の後（練習試合は呼ばない）。</summary>
        public static void AfterOfficialGame(GameResult detail, bool managerWasAway, TournamentKind kind, int matchDay)
        {
            var batting = managerWasAway ? detail.AwayBatting : detail.HomeBatting;
            var pitching = managerWasAway ? detail.AwayPitching : detail.HomePitching;
            Apply(ScoutPromiseJudge.AfterOfficialGame(
                RosterService.Roster, batting, pitching, kind, PitchDayStamp(kind, matchDay)));
        }

        /// <summary>年度替わりの直前（昇級・卒業の前）。</summary>
        public static void AtYearEnd(IReadOnlyList<DevelopingPlayer> roster) => Apply(ScoutPromiseJudge.AtYearEnd(roster));

        /// <summary>4月に入部した直後（寮の約束）。</summary>
        public static void AtJoin(IReadOnlyList<DevelopingPlayer> newcomers)
            => Apply(ScoutPromiseJudge.AtJoin(newcomers, ScoutService.HasDorm));

        /// <summary>新チームの主将を指名した直後。</summary>
        public static void AfterCaptainNamed() => Apply(ScoutPromiseJudge.AfterCaptainNamed(RosterService.Roster));

        /// <summary>新チームの背番号を確定した直後。</summary>
        public static void AfterNumbersDecided() => Apply(ScoutPromiseJudge.AfterNumbersDecided(RosterService.Roster));

        /// <summary>登板日の通し番号（いまの年度・大会・試合日）。オーダー画面の連投の見張りも同じ値を使う。</summary>
        public static int PitchDayStamp(TournamentKind kind, int matchDay)
            => ScoutPromiseJudge.PitchDayStamp(GameClock.YearIndex, kind, matchDay);

        // ── 試合前・試合中の見張り（オーダー画面・交代画面） ──

        /// <summary>いま公式戦の大会中か（練習試合では約束を数えない）。</summary>
        public static bool InOfficialTournament => GameSession.Current.InTournament && !GameSession.Current.Runner.Finished;

        /// <summary>次の公式戦（いまから組む・いま戦っている試合）の登板日の通し番号。試合後の判定と同じ値。</summary>
        public static int NextMatchStamp()
        {
            var gs = GameSession.Current;
            return InOfficialTournament ? PitchDayStamp(gs.Kind, gs.Runner.NextMatchDay) : 0;
        }

        /// <summary>試合中の選手（<see cref="KokoSim.Engine.Players.Player.SourceId"/>）の、生きている約束の持ち主。無ければ null。</summary>
        public static DevelopingPlayer HolderOf(int? sourceId)
        {
            if (sourceId is not { } id || id <= 0) return null;
            foreach (var p in RosterService.Roster)
                if (p.Id == id) return p.PromiseStatus == ScoutPromiseStatus.Active ? p : null;
            return null;
        }

        /// <summary>途中から出すと約束を破るか（公式戦のときだけ）。</summary>
        public static bool BreaksOnEntry(int? sourceId, bool pitches)
        {
            var p = HolderOf(sourceId);
            return p != null && InOfficialTournament
                   && ScoutPromiseJudge.BreaksOnEntry(p, pitches, GameSession.Current.Kind, NextMatchStamp());
        }

        /// <summary>代打を送ると約束を破る打者か（公式戦のときだけ）。</summary>
        public static bool BreaksWhenPinchHitFor(int? sourceId)
        {
            var p = HolderOf(sourceId);
            return p != null && InOfficialTournament && ScoutPromiseJudge.BreaksWhenPinchHitFor(p, GameSession.Current.Kind);
        }

        /// <summary>決着した約束を取り出して空にする（通知フィードへ流す用）。</summary>
        public static List<ScoutPromiseOutcome> DrainNotices()
        {
            var list = new List<ScoutPromiseOutcome>(_notices);
            _notices.Clear();
            return list;
        }

        /// <summary>通知フィードの1行。</summary>
        public static string FeedText(ScoutPromiseOutcome o)
            => o.Kept
                ? o.Player.Name + " との約束（" + ScoutPromises.Label(o.Kind) + "）を守り切った。信頼度 +"
                  + Coeff.PromiseKeptTrust.ToString("0")
                : o.Player.Name + " との約束（" + ScoutPromises.Label(o.Kind) + "）を破った。信頼度 −"
                  + Coeff.PromiseBrokenTrust.ToString("0") + "・名声 −" + Coeff.PromiseBrokenFame.ToString("0");

        /// <summary>新規ゲーム・ロードで通知の残りを捨てる。</summary>
        public static void Reset() => _notices.Clear();

        private static void Apply(IReadOnlyList<ScoutPromiseOutcome> outcomes)
        {
            if (outcomes.Count == 0) return;
            var m = ManagerService.Manager;
            foreach (var o in outcomes)
            {
                if (o.Kept)
                {
                    m.Trust = MathUtil.Clamp(m.Trust + Coeff.PromiseKeptTrust, 0.0, 100.0);
                }
                else
                {
                    m.Trust = MathUtil.Clamp(m.Trust - Coeff.PromiseBrokenTrust, 0.0, 100.0);
                    m.Fame = MathUtil.Clamp(m.Fame - Coeff.PromiseBrokenFame, 0.0, 100.0);
                }
                _notices.Add(o);
            }
        }
    }
}

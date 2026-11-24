using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// センバツ選出校の「地区大会成績」を背景スレッドで作り直す（設計書05 §1.5b・<see cref="KoshienQualifierStatsSim"/>
    /// のセンバツ版）。1スライス＝選出校1校。
    ///
    /// <para><b>なぜ要るか</b>: 他地区の地区大会は集計モデル（<see cref="RegionalNationEngine.RunOtherRegions"/>）で
    /// 順位だけを決めており、選手個人の成績を1つも積んでいない。打ち直すのは<b>選出校が実際に勝ち上がった
    /// 道のりだけ</b>（<see cref="RegionalResult.BracketMatches"/> から <see cref="KnockoutPlacement.PathOf"/> で
    /// 復元）。</para>
    ///
    /// <para><b>対象外（既知の残課題）</b>: 自地区（自校の地区）の他校ぶんは、地区大会が対話/観戦のどちらで
    /// 消化されても <see cref="TournamentRunner"/> 側の <c>BracketMatch</c> を持ち帰る経路が無く未対応
    /// （選出32校のうち多くて数校）。選ばれても「未登板/未出場」のまま＝クラッシュはしない。
    /// 自校自身は <see cref="RegionalEntry.EnterJinguAfterRegionalTournament"/> のスナップショット
    /// （<see cref="Stats.PlayerStatStore.SnapshotRegionalQualifier"/>）が実測を持つので対象外。</para>
    ///
    /// <para>判定は engine の <see cref="KnockoutPlacement"/> / <see cref="QualifierStatsSim"/> にあり、
    /// ここは「いつ・どの順で・どれだけのペースで回すか」だけを持つ（不変条件#3 エンジン純度）。
    /// 乱数は専用 Fork＝本編の乱数列は不変。</para>
    /// </summary>
    public static class SenbatsuQualifierStatsSim
    {
        private const int ManagerSchoolId = -1;
        private const int StepSleepMs = 2;
        private const int PausedPollMs = 16;
        private const int CollectEveryMatches = 48;

        private static readonly int MaxWorkers = BackgroundWork.Parallelism(Math.Max(1, Math.Min(3, Environment.ProcessorCount - 2)));

        private static volatile Task _task;
        private static int _done;
        private static int _nextJob;
        private static volatile int _total;
        private static int _matchesResolved;

        /// <summary>背景の打ち直しがまだ走っているか。</summary>
        public static bool Running
        {
            get { var t = _task; return t != null && !t.IsCompleted; }
        }

        public static int Done => Volatile.Read(ref _done);
        public static int Total => _total;

        public static float Progress
        {
            get { var total = _total; return total <= 0 ? 1f : Math.Min(1f, (float)Done / total); }
        }

        /// <summary>
        /// センバツ選出校の地区大会成績の打ち直しを開始する（32校確定の直後に1回）。
        /// 既に実測がある校（自校・打ち直し済み）・自地区の他校（未対応）は対象から自然に除く。
        /// <paramref name="keepExisting"/> true＝ロード復帰の積み残し用。今年ぶんがセーブから復元済みなので
        /// 帳簿を空にせず、積み終えた校を飛ばして足りない校だけ積む（issue #1116）。
        /// </summary>
        public static void Start(
            IReadOnlyList<int> fieldIds, IReadOnlyList<RegionalResult> otherRegions, int yearIndex, ulong seed,
            bool keepExisting = false)
        {
            // ブラウザ版は打ち直さない（甲子園代表の打ち直しと同じ理由・#1588）。大会展望の地区大会成績が空欄になるだけ。
            if (!BackgroundWork.RunsReplays) return;
            EnsureCompleted();   // 前年ぶんが残っていたら先に畳む（同じ帳簿を2本の走りが触らない）

            var stats = NationService.TournamentStats;
            // 去年の地区大会成績を残さない（残ると下の「実測あり」判定で今年が積まれない）。
            if (!keepExisting) stats.ClearRegionalQualifier();

            // 校ID→その校が実際に出場した地区のブラケット記録。自地区（otherRegions に含まれない）は
            // データを持ち帰れないので対象外（クラス冒頭の「対象外」参照）。
            var matchesByRegion = new Dictionary<int, IReadOnlyList<TournamentMatchRecord>>();
            foreach (var r in otherRegions)
            {
                if (r.BracketMatches.Count == 0) continue;
                foreach (var id in r.PlacementIds)
                    if (!matchesByRegion.ContainsKey(id)) matchesByRegion[id] = r.BracketMatches;
            }

            var pending = new List<int>();
            foreach (var id in fieldIds)
            {
                if (id == ManagerSchoolId) continue;
                var existing = stats.RegionalQualifierForSchool(id);
                if (existing != null && existing.Players.Count > 0) continue;
                if (!matchesByRegion.ContainsKey(id)) continue;
                pending.Add(id);
            }
            if (pending.Count == 0) return;

            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            var rng = new Xoshiro256Random(seed ^ StreamId);
            var brains = new EnemyAiBrainFactory();

            _total = pending.Count;
            Volatile.Write(ref _done, 0);
            Volatile.Write(ref _nextJob, 0);
            Volatile.Write(ref _matchesResolved, 0);

            // 地区大会（秋）成績の打ち直し＝地区大会の開幕月の気温（Issue #803/#971）。
            var weather = WeatherModel.ForTournament(TournamentEntry.Schedule, TournamentKind.Regional);
            var workers = new Task[MaxWorkers];
            for (var w = 0; w < workers.Length; w++)
            {
                var resolver = new BackgroundMatchResolver(
                    NationService.Rosters, new GameContext { Weather = weather }, yearIndex, stats: null,
                    modernRules: null, calendarYear: calendarYear, brains: brains);
                workers[w] = BackgroundWork.RunSteps(() => Step(pending, matchesByRegion, stats, rng, yearIndex, resolver));
            }
            _task = Task.WhenAll(workers);
        }

        /// <summary>本編・県予選（KSQP）と衝突しない専用ストリーム識別子（'SBQP'＝SenBatsu Qualifier Pass）。</summary>
        private const ulong StreamId = 0x5342_5150UL;

        /// <summary>1歩＝1校。まだ校が残っていれば true を返す（ブラウザ版はコマごとに少しずつ回す。#1588）。</summary>
        private static bool Step(
            IReadOnlyList<int> pending, IReadOnlyDictionary<int, IReadOnlyList<TournamentMatchRecord>> matchesByRegion,
            NationTournamentStats stats, IRandomSource rng, int yearIndex, BackgroundMatchResolver resolver)
        {
            PauseGate();

            var idx = Interlocked.Increment(ref _nextJob) - 1;
            if (idx >= pending.Count) return false;

            var schoolId = pending[idx];
            var matches = matchesByRegion[schoolId];
            var path = KnockoutPlacement.PathOf(matches, schoolId);
            var totalRounds = matches.Count > 0 ? matches.Max(m => m.RoundsRemaining) : 1;
            QualifierStatsSim.ReplayPath(
                schoolId, path, NationService.SchoolById, resolver, stats.FoldRegionalQualifier,
                TournamentEntry.Schedule, totalRounds, yearIndex, rng, afterMatch: PauseGate);

            Interlocked.Increment(ref _done);
            BackgroundWork.Sleep(StepSleepMs);
            return true;
        }

        private static void PauseGate()
        {
            var m = Interlocked.Increment(ref _matchesResolved);
            if (m % CollectEveryMatches == 0) GC.Collect();

            while (BackgroundWork.Threaded && NationBackgroundSim.Throttle == NationBackgroundSim.SimThrottle.Paused)
                BackgroundWork.Sleep(PausedPollMs);
        }

        /// <summary>打ち直しを完了まで待つ（ロスター変更の前に必ず呼ぶ）。未開始なら即返る。</summary>
        public static void EnsureCompleted()
        {
            var t = _task;
            if (t == null) return;
            try { BackgroundWork.Wait(t); }
            catch (AggregateException e) { UnityEngine.Debug.LogException(e.GetBaseException()); }   // 個々の校の失敗は握り潰す（読み物なので進行は止めない）が、ログは出す（issue #1226）
            _task = null;
            _total = 0;
            Volatile.Write(ref _done, 0);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 全国47県の裏試合フルシム（自県以外）をバックグラウンドスレッドで回す（設計書05 §1.4 / #43）。
    /// エンジンは Unity 非依存＝スレッドで安全に回せる。共有する NationRosters / NationTournamentStats は
    /// スレッド安全化済み（自県はメインスレッドの TournamentRunner が同一 stats へ積む・県キーは disjoint）。
    /// UnityEngine API は一切触らない（Task 内でのメインスレッド依存を持ち込まない）。
    /// </summary>
    public static class NationBackgroundSim
    {
        /// <summary>直近に開始した全国裏試合タスク（次の節目までに完了させるために保持）。</summary>
        public static Task<IReadOnlyList<PrefectureResult>> Current { get; private set; }

        /// <summary>
        /// 他県（<paramref name="excludePrefectureId"/> 以外）の夏の地方大会を一括フルシムで開始する。
        /// 自県は呼び出し側（HomeState の TournamentRunner）が対話的に消化して同一 stats へ積むため除外する。
        /// </summary>
        public static void Start(
            Nation nation, NationRosters rosters, NationTournamentStats stats,
            NationCoefficients coeff, TournamentSchedule schedule, int yearIndex, int calendarYear,
            int excludePrefectureId, ulong seed, IEnemyBrainFactory brains)
        {
            var rng = new Xoshiro256Random(seed ^ 0x4E37_1000UL);
            var ctx = new GameContext();
            Current = Task.Run(() => NationalTournamentEngine.RunSummer(
                nation, rosters, ctx, coeff, schedule, yearIndex, stats, rng,
                excludePrefectureId: excludePrefectureId, modernRules: null, calendarYear: calendarYear,
                brains: brains));
        }

        /// <summary>
        /// 実行中の全国裏試合を完了まで待つ（成長の節目＝メインスレッドのロスター変更の前に必ず呼ぶ。
        /// 背景タスクと成長のロスター変更が重ならないようにするため）。未開始なら即返る。
        /// </summary>
        public static void EnsureCompleted()
        {
            var t = Current;
            if (t == null) return;
            try { t.Wait(); }
            catch (AggregateException) { /* 個々の県の失敗は握り潰す（大会全体は継続。詳細はログ層で） */ }
            Current = null;
        }
    }
}

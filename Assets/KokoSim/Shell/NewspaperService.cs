using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 地方大会新聞（設計書05 §1.4・issue #1485）。自校の県大会（夏・秋）で記録された裏試合を貯め、
    /// ホームの通知フィードへ流す行に直す。読み取り専用＝乱数・試合結果には触れない。
    /// ブラケットの残り消化は背景スレッドで走るので、貯める側はロックする。セーブには載せない（流した行はフィードにだけ残る）。
    /// </summary>
    public static class NewspaperService
    {
        private static readonly object Gate = new object();
        private static readonly List<BackgroundGameReport> Pending = new List<BackgroundGameReport>();

        /// <summary>この大会の裏試合を新聞の材料にするか。自県の勝ち抜き戦（夏・秋の県大会）だけ。</summary>
        public static bool Covers(TournamentKind kind) => kind == TournamentKind.Summer || kind == TournamentKind.Autumn;

        /// <summary>自校の大会の進行体へ観測口をつなぐ（<see cref="GameSession.EnterTournament"/>／復帰から呼ぶ）。</summary>
        public static void Attach(TournamentKind kind, ITournamentRunner runner)
        {
            lock (Gate) Pending.Clear();
            if (runner is TournamentRunner r && Covers(kind)) r.OnBackgroundGame = Collect;
        }

        private static void Collect(BackgroundGameReport report)
        {
            lock (Gate) Pending.Add(report);
        }

        /// <summary>貯まった裏試合を新聞の行に直して返し、空にする（メインスレッドから）。</summary>
        public static IReadOnlyList<NewspaperLine> Drain()
        {
            List<BackgroundGameReport> batch;
            lock (Gate)
            {
                if (Pending.Count == 0) return System.Array.Empty<NewspaperLine>();
                batch = new List<BackgroundGameReport>(Pending);
                Pending.Clear();
            }
            return TournamentNewspaper.Compose(batch, PhenomOf);
        }

        private static PhenomType PhenomOf(int schoolId, int sourceId)
            => NationService.Rosters.TryGet(schoolId, out var roster)
                ? roster.Players.FirstOrDefault(p => p.Id == sourceId)?.Phenom ?? PhenomType.None
                : PhenomType.None;
    }
}

using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 夏/秋の大会モードへの遷移（engine/session の実処理）を全画面共通の1経路に集約する（issue #134）。
    /// 以前は <see cref="KokoSim.Unity.Home.HomeState"/>.AdvanceWeek だけがこの判定を持っており、
    /// 練習・大会・練習試合・選手・メンバーの各タブから週を進めると大会入りを素通りしていた
    /// （＝開幕演出が出ず・試合未消化のまま3年引退）。共通クロック <see cref="GameClock"/> の週入りフックが
    /// この <see cref="Enter"/> を呼ぶことで、どのタブから週を進めても必ず大会モードへ入る。
    /// UI演出（開幕バナー・試合ダイアログ）はここでは行わず、<see cref="GameSession"/> のフラグ
    /// （BannerPending 等）を Home 側が拾って出す＝遷移とUI提示を分離する。
    /// </summary>
    public static class TournamentEntry
    {
        private static readonly NationCoefficients NationCoeff = new NationCoefficients();
        private static readonly TournamentSchedule Schedule = new TournamentSchedule();
        private const int ManagerSchoolId = -1;      // 自校（生成校と衝突しない専用ID）
        private const int FieldPrefectureId = 13;    // 神奈川（Prefecture.Id は0基点＝JIS番号-1）

        /// <summary>
        /// 大会モードへ入る（進行体＋全国裏試合フルシムを構築して <see cref="GameSession"/> へ公開する）。
        /// 自県の裏試合は永続ロスターの GameEngine.Play で対話的に消化し、他県は背景でフルシムする（#43）。
        /// </summary>
        public static void Enter(TournamentKind kind)
        {
            var manager = BuildManagerSchool();
            var field = BuildField(manager);
            var yearIndex = GameClock.YearIndex;
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            // 大会シードは母種（プレイ毎に変動）＋年・種別で導出。同じ母種なら完全再現される（決定論は維持）。
            var seed = GameSeed.Master ^ (ulong)(9000 + yearIndex * 10 + (int)kind);

            // 裏試合フルシム（#43）: 全国通算成績の今大会スコープをリセットし、自県の裏試合を永続ロスターの
            // GameEngine.Play で解決する resolver を用意する（成績を全国集計へ積む・敵AI采配を注入）。
            var stats = NationService.TournamentStats;
            stats.StartTournament();
            var brains = new EnemyAiBrainFactory();
            var homeBg = new BackgroundMatchResolver(NationService.Rosters, new GameContext(), yearIndex, stats,
                modernRules: null, calendarYear: calendarYear, brains: brains);

            // 自校の一戦は詳細試合エンジンで解決（PlayerMatchResolver）。自県の裏試合は homeBg でフルシム。
            var runner = new TournamentRunner(field, manager, NationCoeff, new Xoshiro256Random(seed), Schedule,
                TournamentTitle(kind), new PlayerMatchResolver(), homeBg);
            // field も渡す（大会展望が実際の出場校＝自校＋県内校を引くため）。
            GameSession.Current.EnterTournament(kind, TournamentTitle(kind), runner, field);
            GameSession.Current.Year = yearIndex;

            // 他県（全国46県）の裏試合をバックグラウンドでフルシム（自県は上の runner が対話的に消化＝除外）。
            NationBackgroundSim.Start(NationService.Nation, NationService.Rosters, stats, NationCoeff, Schedule,
                yearIndex, calendarYear, NationService.PrefectureId, seed, brains);
        }

        /// <summary>開幕バナー用の大会名（例「2028年 選手権神奈川大会」）。ホームのバナー表示と同一の表記。</summary>
        public static string TournamentTitle(TournamentKind kind)
        {
            var year = SeasonClock.SeasonBaseYear + (GameClock.YearIndex - 1);
            return kind == TournamentKind.Summer
                ? year + "年 選手権神奈川大会"
                : year + "年 秋季神奈川県大会";
        }

        private static School BuildManagerSchool()
        {
            var trainable = RosterService.Active.Where(p => p.Grade <= 3).ToList();
            // 総合＝6指標のリーグ標準化総合（③）。
            var strength = trainable.Count == 0 ? 40.0 : TeamOverall.Of(trainable);
            return new School { Id = ManagerSchoolId, Name = "桜丘", PrefectureId = FieldPrefectureId, Strength = strength };
        }

        private static List<School> BuildField(School manager)
        {
            // 母集団は NationService（全画面の単一ソース）。練習試合の申込先と同じ School を引く。
            var field = new List<School> { manager };
            foreach (var s in NationService.PrefectureSchools)
            {
                if (s.Id == manager.Id) continue;
                field.Add(s);
            }
            return field;
        }
    }
}

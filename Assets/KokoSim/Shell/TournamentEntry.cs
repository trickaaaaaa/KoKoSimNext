using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
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

        /// <summary>
        /// 大会日程（issue #583）。進行体の構築と<b>表示（実月日）で同じ実体を使う</b>＝日割りと
        /// 開幕月日が2箇所に散らないようにする。C#既定＝data/coefficients.yaml tournament: と同値。
        /// </summary>
        public static TournamentSchedule Schedule { get; } = new TournamentSchedule();
        private const int ManagerSchoolId = -1;      // 自校（生成校と衝突しない専用ID）

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
            // 自県の裏カードはメモ化 resolver で包む＝プレフェッチ（GameSession が暇な時間に背景で先行解決）で温めた
            // 結果を、本解決（BeginRound/ResolveRound）がキャッシュ命中で即座に再利用する（#試合開始前ロード短縮）。
            // ⚠ stats はここで渡さない（resolver に畳ませない）＝プレフェッチは「計算するだけ」にする。
            //   全国成績への畳み込みはランナーが裏カードを**記録した瞬間**に行う（下の onBackgroundMatch）。
            //   計算時に畳むと、大会中セーブ（issue #565）を挟んだときにメモ化キャッシュが消え、
            //   同じカードがロード後にもう一度畳まれる＝二重計上になる。
            var homeBg = new MemoizingBackgroundResolver(
                new BackgroundMatchResolver(NationService.Rosters, new GameContext(), yearIndex, stats: null,
                    modernRules: null, calendarYear: calendarYear, brains: brains));

            // 自校の一戦は詳細試合エンジンで解決（PlayerMatchResolver）。自県の裏試合は homeBg でフルシム。
            var runner = new TournamentRunner(field, manager, NationCoeff, new Xoshiro256Random(seed), Schedule,
                TournamentTitle(kind), new PlayerMatchResolver(), homeBg,
                onBackgroundMatch: (awayId, homeId, g) => stats.FoldMatch(awayId, homeId, g));
            // field も渡す（大会展望が実際の出場校＝自校＋県内校を引くため）。
            GameSession.Current.EnterTournament(kind, TournamentTitle(kind), runner, field);
            GameSession.Current.Year = yearIndex;

            // 他県（全国46県）の裏試合をバックグラウンドでフルシム（自県は上の runner が対話的に消化＝除外）。
            NationBackgroundSim.Start(NationService.Nation, NationService.Rosters, stats, NationCoeff, Schedule,
                yearIndex, calendarYear, NationService.PrefectureId, seed, brains);
        }

        /// <summary>
        /// セーブから大会モードへ復帰する（issue #565 F1）。<see cref="Enter"/> と同じ部品で resolver を
        /// 組み直し、進行体だけをセーブの状態から作る。
        ///
        /// <para><b>ここで <c>stats.StartTournament()</c> を呼んではいけない</b>＝今大会成績は
        /// <see cref="NationService.Restore"/> で復元済みで、呼ぶとその場で全部消える。</para>
        ///
        /// <para><b>全国裏試合（<see cref="NationBackgroundSim"/>）も起動しない</b>。保存は他県の消化を
        /// 待ってから行う（<see cref="SaveGameIO.Save"/>）ので、復元時点で全46県は消化済み。ここで
        /// 起動し直すと同じ大会を二度積む＝全国成績が倍になる。</para>
        /// </summary>
        public static void RestoreRunner(TournamentSaveState t)
        {
            if (t == null) return;
            var manager = BuildManagerSchool();
            var field = ResolveField(t.FieldSchoolIds, manager);
            var yearIndex = GameClock.YearIndex;
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            var brains = new EnemyAiBrainFactory();
            var stats = NationService.TournamentStats;   // 復元済み＝StartTournament は呼ばない
            var homeBg = new MemoizingBackgroundResolver(
                new BackgroundMatchResolver(NationService.Rosters, new GameContext(), yearIndex, stats: null,
                    modernRules: null, calendarYear: calendarYear, brains: brains));

            var runner = TournamentRunner.FromSaved(t.Runner, field, manager, NationCoeff, Schedule,
                new PlayerMatchResolver(), homeBg,
                onBackgroundMatch: (awayId, homeId, g) => stats.FoldMatch(awayId, homeId, g));
            GameSession.Current.RestoreTournament(t, runner, field);
        }

        /// <summary>
        /// 保存された出場校 Id を School へ戻す。自校（Id=-1）は現在のロスターから組み直す
        /// （強さは実戦成長で動くが、シード配置は保存された並びを使うので樹形図はずれない）。
        /// 見つからない Id は捨てず例外にする＝黙って出場校が減った大会を作らない。
        /// </summary>
        private static List<School> ResolveField(List<int> ids, School manager)
        {
            var field = new List<School>(ids?.Count ?? 0);
            if (ids == null) return field;
            foreach (var id in ids)
            {
                if (id == manager.Id) { field.Add(manager); continue; }
                var s = NationService.SchoolById(id);
                if (s == null) throw new InvalidOperationException($"セーブの出場校 Id={id} が全国に見つかりません。");
                field.Add(s);
            }
            return field;
        }

        /// <summary>
        /// 開幕バナー用の大会名（例「2028年 選手権神奈川大会」）。ホームのバナー表示と同一の表記。
        /// 県は自校の本拠県から引く（issue #585 で可変になった）。夏は短縮形（神奈川）、
        /// 秋は正式名（神奈川県）＝実在の大会名の慣習に合わせる。
        /// </summary>
        public static string TournamentTitle(TournamentKind kind)
        {
            var year = SeasonClock.SeasonBaseYear + (GameClock.YearIndex - 1);
            var pref = NationService.PrefectureId;
            return kind == TournamentKind.Summer
                ? year + "年 選手権" + PrefectureNames.ShortOf(pref) + "大会"
                : year + "年 秋季" + PrefectureNames.JapaneseOf(pref) + "大会";
        }

        private static School BuildManagerSchool()
        {
            var trainable = RosterService.Active.Where(p => p.Grade <= 3).ToList();
            // 総合＝6指標のリーグ標準化総合（③）。
            var strength = trainable.Count == 0 ? 40.0 : TeamOverall.Of(trainable);
            return new School
            {
                Id = ManagerSchoolId, Name = NationService.ManagerSchoolName,
                PrefectureId = NationService.PrefectureId, DistrictId = NationService.DistrictId,
                Strength = strength,
            };
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

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
using KokoSim.Engine.Stats;

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
            // 夏だけ北海道・東京の区画（北北海道/南北海道・東東京/西東京）を意識する。秋は47県のまま
            // ＝region=null にして従来どおり県全体を1大会にする（SummerRegions は夏の予選単位の概念）。
            var region = kind == TournamentKind.Summer
                ? SummerRegions.RegionFor(NationService.Nation.Prefectures, manager.PrefectureId, manager.DistrictId)
                : (SummerRegion?)null;
            var field = BuildField(manager, region);
            var yearIndex = GameClock.YearIndex;
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            // 大会シードは母種（プレイ毎に変動）＋年・種別で導出。同じ母種なら完全再現される（決定論は維持）。
            var seed = TournamentSeed(kind, yearIndex);

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

            // 他県（全国46〜48県）の裏試合をバックグラウンドでフルシム（自県の区画は上の runner が
            // 対話的に消化＝除外。分割県はもう片方の区画だけ背景で消化され独自の代表を出す）。
            // 夏だけ＝これは夏フォーマット（NationalTournamentEngine.BeginSummer）の49代表決めなので
            // 秋の入場でも呼ぶと「無関係な夏ブラケットを毎年もう一度引く」だけの無駄になる。かつては
            // kind を問わず毎回呼んでいたため、秋の入場のたびに AiSchoolYearLedger.AddWins（AI校進化の
            // 勝数シグナル）へ夏ぶんが二重に積まれていた（docs/design/OPEN-QUESTIONS.md 2026-08-22
            // で発見・実測で影響は小さいと確認＝15〜30年シムで標準偏差の広がりが約1.03〜1.04倍）。
            if (kind == TournamentKind.Summer) StartSummerBackgroundSim(yearIndex, calendarYear, stats, brains);

            // 秋季県大会は地区大会の出場枠判定に順位（優勝校だけでなく）が要るため、47県ぶんを別途
            // 順位付きで背景シムする（設計書05 §1.5b・RegionalEntry が読む）。夏は使わない。
            if (kind == TournamentKind.Autumn) StartAutumnBackgroundSim(yearIndex, calendarYear, stats, brains);
        }

        /// <summary>
        /// 秋季全国裏試合（47県ぶんの順位付き県大会）の起動。<see cref="Enter"/> の秋分岐と
        /// <see cref="RestoreRunner"/> の追いつき実行が<b>同じシード式・同じ引数</b>で通る唯一の経路。
        /// シードを2箇所に書き写すと、ロード復帰の結果がセーブ前と食い違って樹形図がずれる。
        /// </summary>
        private static void StartAutumnBackgroundSim(
            int yearIndex, int calendarYear, NationTournamentStats stats, IEnemyBrainFactory brains,
            bool collectGarbage = true)
        {
            AutumnBackgroundSim.Start(NationService.Nation, NationService.Rosters, stats, NationCoeff, Schedule,
                yearIndex, calendarYear, NationService.PrefectureId, AutumnBackgroundSeed(yearIndex),
                brains, collectGarbage);
        }

        /// <summary>秋季全国裏試合のシード。母種＋年から導出＝<b>この大会の同定でもある</b>
        /// （<see cref="AutumnBackgroundSim.HasResultsFor"/> が手元の結果と突き合わせる鍵）。</summary>
        private static ulong AutumnBackgroundSeed(int yearIndex)
            => GameSeed.Master ^ (ulong)(9400 + yearIndex * 10 + (int)TournamentKind.Autumn);

        /// <summary>
        /// 夏の全国裏試合（49区画ぶんの代表決め）の起動。<see cref="StartAutumnBackgroundSim"/> と同型だが、
        /// <b>校別勝数を年間台帳へ積む</b>ぶんだけ扱いが重い＝積み直しでは <paramref name="foldLedger"/> を
        /// false にしないと AI校進化の勝数が倍になる。
        /// </summary>
        private static void StartSummerBackgroundSim(
            int yearIndex, int calendarYear, NationTournamentStats stats, IEnemyBrainFactory brains,
            bool foldLedger = true, bool collectGarbage = true)
        {
            // 自校の区画（北海道・東京は分割）だけを除外する。Enter と同じ導出でないと、
            // 積み直しで自校の区画がもう一度背景消化され代表が二重に出る（BuildManagerSchool の
            // 県・地区は NationService の同定そのものなので、ここでは直接引く）。
            var region = SummerRegions.RegionFor(
                NationService.Nation.Prefectures, NationService.PrefectureId, NationService.DistrictId);
            NationBackgroundSim.Start(NationService.Nation, NationService.Rosters, stats, NationCoeff, Schedule,
                yearIndex, calendarYear, NationService.PrefectureId, region?.Split,
                SummerBackgroundSeed(yearIndex), brains, foldLedger, collectGarbage);
        }

        /// <summary>夏の全国裏試合のシード。<b>大会シードそのもの</b>（自校の進行体と同じ値）なので
        /// <see cref="TournamentSeed"/> から引く＝<see cref="Enter"/> と食い違いようがない形にしておく。</summary>
        private static ulong SummerBackgroundSeed(int yearIndex)
            => TournamentSeed(TournamentKind.Summer, yearIndex);

        /// <summary>大会シード（母種＋年＋種別）。<see cref="Enter"/> の進行体と
        /// <see cref="SummerBackgroundSeed"/> の唯一の出どころ。</summary>
        private static ulong TournamentSeed(TournamentKind kind, int yearIndex)
            => GameSeed.Master ^ (ulong)(9000 + yearIndex * 10 + (int)kind);

        /// <summary>
        /// セーブから大会モードへ復帰する（issue #565 F1）。<see cref="Enter"/> と同じ部品で resolver を
        /// 組み直し、進行体だけをセーブの状態から作る。
        ///
        /// <para><b>ここで <c>stats.StartTournament()</c> を呼んではいけない</b>＝今大会成績は
        /// <see cref="NationService.Restore"/> で復元済みで、呼ぶとその場で全部消える。</para>
        ///
        /// <para><b>全国裏試合（夏 <see cref="NationBackgroundSim"/>・秋 <see cref="AutumnBackgroundSim"/>）は
        /// 起動し直す</b>。結果はセーブに載らない static なので、Play を起動し直してからロードすると空になり、
        /// 夏は「甲子園が開かれない」・秋は「自県ぶんだけの2校の地区大会」になる。同じシードなら結果が
        /// バイト一致する（base rng は Fork のみで進めない）ので、セーブへ焼かず再計算で戻す。</para>
        ///
        /// <para>⚠ <b>夏は <c>foldLedger:false</c> で積み直す</b>。<c>AiSchoolYearLedger.AddWins</c>
        /// （AI校進化の勝数シグナル）はセーブ（<c>nationYearWins</c>）から復元済みなので、
        /// ここで積むと勝数が倍になる。秋は勝数を持たないのでこの区別が要らない。</para>
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

            // 秋の連鎖（県大会→地区大会→明治神宮）の途中で復帰したときは、47県ぶんの秋季県大会を
            // 背景で積み直す。結果はセーブに載らない static なので、Play を起動し直してからロードすると
            // 空のまま地区大会が組まれ、自県ぶんだけの「2校の地区大会」になる（2040年秋に実機発症）。
            //
            // 地区大会・明治神宮での復帰でも要る＝JinguEntry.ComputeOtherRegions が他9地区を組むときに
            // 同じ結果を読むため。**待たない**のが要点で、通常経路（Enter）も大会入場時に背景起動して
            // 待つのは県大会が終わる瞬間の EnsureCompleted() だけ。復帰時点ではまだ試合が残っているので、
            // 試合を1つ消化する間に終わる＝ロード時間は伸びない（47県で実測80ms）。
            if (t.Kind == TournamentKind.Autumn || t.Kind == TournamentKind.Regional
                || t.Kind == TournamentKind.Jingu)
            {
                if (!AutumnBackgroundSim.HasResultsFor(AutumnBackgroundSeed(yearIndex)))
                    StartAutumnBackgroundSim(yearIndex, calendarYear, stats, brains, collectGarbage: false);
            }

            // 夏の県大会の途中で復帰したときも同じ理由で積み直す（49代表が空だと甲子園が開かれない）。
            // 秋と違うのは校別勝数の扱いだけ＝勝数はセーブ（nationYearWins）から復元済みなので
            // foldLedger:false で積まない。積むと AI校進化の勝数が倍になる。
            //
            // 甲子園本戦（SummerKoshien）での復帰では要らない。49代表は進行体（fieldSchoolIds）に
            // 焼かれており、KoshienEntry.EnterAfterPrefectureTournament はもう走らないため。
            if (t.Kind == TournamentKind.Summer
                && !NationBackgroundSim.HasResultsFor(SummerBackgroundSeed(yearIndex)))
            {
                StartSummerBackgroundSim(
                    yearIndex, calendarYear, stats, brains, foldLedger: false, collectGarbage: false);
            }
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

        /// <summary>
        /// 出場校。<paramref name="region"/> が付いている（＝夏・分割県）ときは自校の区画だけに絞る
        /// （<see cref="SummerRegions.Entrants"/>）。null（秋、または夏の非分割県）は従来どおり
        /// <see cref="NationService.PrefectureSchools"/>＝県全体（両者は非分割県では同じ中身になる）。
        /// </summary>
        private static List<School> BuildField(School manager, SummerRegion? region)
        {
            // 母集団は NationService（全画面の単一ソース）。練習試合の申込先と同じ School を引く。
            var pool = region is { } r
                ? SummerRegions.Entrants(NationService.Nation, r)
                : NationService.PrefectureSchools;
            var field = new List<School> { manager };
            foreach (var s in pool)
            {
                if (s.Id == manager.Id) continue;
                field.Add(s);
            }
            return field;
        }
    }
}

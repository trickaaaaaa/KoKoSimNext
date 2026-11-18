using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 秋季地区予選（神奈川7地区・兵庫5地区・愛知5地区・北海道10支部等、地理固定割の県だけ）から
    /// 秋季県大会（<see cref="TournamentKind.Autumn"/>）への遷移。<see cref="KoshienEntry"/>／
    /// <see cref="RegionalEntry"/> と同型のシェル層クラス（issue autumn-district-tournament-not-wired）。
    ///
    /// <para><b>暦の週では始まらない。</b> 秋季地区予選の後始末（<c>HomeState.DismissResult</c>）から
    /// 直接つなぐ（夏の甲子園と同じ理由＝暦の開幕週を作らない）。</para>
    ///
    /// <para>自校の地区は <see cref="TournamentEntry"/> が対話プレイで消化する（1つの
    /// <see cref="TournamentRunner"/>）。同じ県の他地区は、自校の地区予選を遊んでいる間に
    /// <see cref="OtherDistrictBackgroundSim"/> が<b>1球フルシム</b>で解いておく（issue #1106）＝勝ち上がってきた
    /// 相手校の選手成績が全国の成績帳に積まれる。解いた結果はここ（地区予選の後始末）で受け取り、成績を1回だけ積む。</para>
    /// </summary>
    public static class DistrictEntry
    {
        private static readonly NationCoefficients NationCoeff = new NationCoefficients();
        private const int ManagerSchoolId = -1;

        /// <summary>秋季地区予選が終わった直後に呼ぶ。<b>地区予選以外・自校非対話モードなら何もしない</b>。</summary>
        /// <returns>自校が秋季県大会に出場した（＝対話モードで入った）か。</returns>
        public static bool EnterAfterDistrictTournament(TournamentKind endedKind)
        {
            if (endedKind != TournamentKind.AutumnDistrict) return false;
            if (GameSession.Current.Mode != GameMode.Normal) return false;

            // 自地区の残りブラケット（自校非関与の裏カード）が未消化なら待つ（KoshienEntry と同じ理由）。
            BracketFinishSim.EnsureCompleted();
            GameSession.Current.FlushPendingBracketFold();

            var myChampionId = GameSession.Current.LastDistrictChampionId;
            if (myChampionId == null) return false;   // 異常系。地区予選を飛ばす。

            var prefectureId = NationService.PrefectureId;
            var districtCount = DistrictPlanProvider.AutumnDistrictsOf(prefectureId);
            var advance = DistrictPlanProvider.AdvancePerDistrictOf(prefectureId);
            var yearIndex = GameClock.YearIndex;
            // 自地区の進出校。旧セーブ（v21以前）は一覧が無く優勝校1校だけが残っているのでそれを使う。
            var myQualifierIds = GameSession.Current.LastDistrictQualifierIds is { Count: > 0 } ids
                ? ids : new[] { myChampionId.Value };
            var otherDistricts = ResolveOtherDistricts(prefectureId, districtCount, advance, yearIndex);
            var field = CollectDistrictQualifiers(districtCount, myQualifierIds, otherDistricts);
            AddSummerChampionSeed(field, prefectureId);
            if (field.Count < 2)
            {
                UnityEngine.Debug.LogWarning(
                    $"[秋季地区予選] {PrefectureNames.JapaneseOf(prefectureId)}の秋季県大会出場校が "
                    + $"{field.Count} 校＝開催できない（地区代表が揃っていない）。");
                return false;
            }

            var manager = ManagerSchool();
            var title = TournamentEntry.TournamentTitle(TournamentKind.Autumn);
            var seed = TournamentEntry.TournamentSeed(TournamentKind.Autumn, yearIndex);

            // 自校が地区予選で県大会進出圏外だった年は出場しない（issue #688）。TournamentRunner は自校が
            // 出場校に居ないと投げるので、観るだけで県大会を同期解決し、その順位を地区大会の判定へ渡す。
            if (field.All(s => s.Id != manager.Id))
            {
                var spectated = TournamentRunner.Spectated(
                    field, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule, title);
                spectated.DrainBracketResolution();
                var byId = field.ToDictionary(s => s.Id);
                var placementIds = KnockoutPlacement.FromMatches(spectated.BuildBracketView().Matches, id => byId[id])
                    .Select(s => s.Id).ToList();
                GameSession.Current.SetSpectatedAutumnPlacement(placementIds);
                // 通常の県大会の直後に走る地区大会以降の連鎖を、ここから直接起動する。
                RegionalEntry.EnterAfterPrefectureTournament(TournamentKind.Autumn);
                return false;
            }

            // 今大会スコープをリセットしてから積む（地区予選の成績を秋季県大会の「今大会」に残さない）。
            NationService.TournamentStats.StartTournament();
            var runner = new TournamentRunner(field, manager, NationCoeff, new Xoshiro256Random(seed),
                TournamentEntry.Schedule, title, new PlayerMatchResolver(),
                TournamentEntry.BackgroundResolverFor(TournamentKind.Autumn, yearIndex),
                onBackgroundMatch: TournamentEntry.FoldBackgroundMatch);
            GameSession.Current.EnterTournament(TournamentKind.Autumn, title, runner, field);
            GameSession.Current.Year = yearIndex;
            return true;
        }

        /// <summary>自校の地区の進出校＋他地区の進出校を、地区番号 0..districtCount-1 の並びで集める。</summary>
        private static List<School> CollectDistrictQualifiers(
            int districtCount, IReadOnlyList<int> myQualifierIds,
            IReadOnlyDictionary<int, OtherDistrictResult> otherDistricts)
        {
            var manager = ManagerSchool();
            var field = new List<School>();
            for (var d = 0; d < districtCount; d++)
            {
                if (d == (manager.DistrictId ?? 0))
                {
                    foreach (var id in myQualifierIds)
                    {
                        var mine = id == ManagerSchoolId ? manager : NationService.SchoolById(id);
                        if (mine != null) field.Add(mine);
                    }
                    continue;
                }

                if (otherDistricts.TryGetValue(d, out var other)) field.AddRange(other.Qualifiers);
            }
            return field;
        }

        /// <summary>
        /// 夏の代表校（<see cref="GameSession.LastPrefChampionId"/>）を県大会へ推薦する（<c>seed_exemption</c>・
        /// 設計書05 §1.5）。地区予選の進出校に既に居れば足さない。夏を優勝していない年（未確定）は何もしない。
        /// </summary>
        private static void AddSummerChampionSeed(List<School> field, int prefectureId)
        {
            if (!DistrictPlanProvider.SeedExemptionOf(prefectureId)) return;
            if (GameSession.Current.LastPrefChampionId is not int id) return;
            if (field.Any(s => s.Id == id)) return;
            var school = id == ManagerSchoolId ? ManagerSchool() : NationService.SchoolById(id);
            if (school != null) field.Add(school);
        }

        /// <summary>
        /// 地区の出場校をブロックへ分ける（ブロックリーグ戦の県・issue #772）。夏の県大会の順位でシード分散し、
        /// 抽選は大会シードから Fork した専用ストリーム（進行体の乱数列とは別）。自校の地区（対話）と
        /// 他地区（<see cref="ResolveOtherDistrict"/>）が同じ規則で組む唯一の入口。
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<School>> DrawBlocks(IReadOnlyList<School> entrants, int prefectureId, ulong seed)
            => BlockDraw.Split(entrants, DistrictPlanProvider.TeamsPerBlockOf(prefectureId), SummerSeedRank(),
                new Xoshiro256Random(seed).Fork(BlockDraw.DrawStreamId));

        /// <summary>夏の県大会の順位（0＝優勝）。控えが無い年（初年度・旧セーブ）は null＝全校抽選。</summary>
        private static Func<School, int?> SummerSeedRank()
        {
            var ids = GameSession.Current.LastSummerPlacementIds;
            if (ids == null || ids.Count == 0) return null;
            var rank = new Dictionary<int, int>();
            for (var i = 0; i < ids.Count; i++) if (!rank.ContainsKey(ids[i])) rank[ids[i]] = i;
            return s => rank.TryGetValue(s.Id, out var r) ? r : (int?)null;
        }

        /// <summary>他地区の消化を背景で始める（自校の地区予選に入るとき・その途中でロード復帰したとき）。
        /// <para><b>大会モードかどうかを見ない</b>。呼ばれるのはどれも自校が地区予選の大会モードに入った後
        /// （<see cref="TournamentEntry.Enter"/> は EnterTournament の後、ロード復帰は RestoreTournament の後）で、
        /// <see cref="EnterAfterDistrictTournament"/> と同じ「Normal でなければ戻る」を置くと一度も起動せず、
        /// 県大会へ入る瞬間に全地区を同期で解く（神奈川で237試合・エディタ実測で約37秒止まる）ことになっていた（PR #1117 のレビュー）。</para></summary>
        public static void StartOtherDistrictSim()
        {
            var prefectureId = NationService.PrefectureId;
            var districtCount = DistrictPlanProvider.AutumnDistrictsOf(prefectureId);
            if (districtCount <= 0) return;
            var yearIndex = GameClock.YearIndex;
            OtherDistrictBackgroundSim.Start(OtherDistrictKey(yearIndex),
                BuildOtherDistrictJobs(prefectureId, districtCount, DistrictPlanProvider.AdvancePerDistrictOf(prefectureId),
                    yearIndex));
        }

        /// <summary>他地区の結果（背景で解いた分）を揃える。背景が走っていなければここで始め、同期で解く（異常系の保険）。</summary>
        private static IReadOnlyDictionary<int, OtherDistrictResult> ResolveOtherDistricts(
            int prefectureId, int districtCount, int advance, int yearIndex)
        {
            if (!OtherDistrictBackgroundSim.IsFor(OtherDistrictKey(yearIndex))) StartOtherDistrictSim();
            var results = OtherDistrictBackgroundSim.WaitForResults(OtherDistrictKey(yearIndex));

            // 解けなかった地区（背景の失敗）は同期で解き直す。決定論なので同じ結果になる。
            var jobs = BuildOtherDistrictJobs(prefectureId, districtCount, advance, yearIndex);
            if (jobs.Any(j => !results.ContainsKey(j.Key)))
            {
                var filled = new Dictionary<int, OtherDistrictResult>(results);
                foreach (var j in jobs) if (!filled.ContainsKey(j.Key)) filled[j.Key] = j.Value();
                results = filled;
            }

            OtherDistrictBackgroundSim.FoldInto(NationService.TournamentStats, results);
            return results;
        }

        /// <summary>今年の他地区の背景フルシムがもう始まっているか（開発用の通し確認 DistrictFlowCheck が見る）。</summary>
        public static bool OtherDistrictSimStartedThisYear
            => OtherDistrictBackgroundSim.IsFor(OtherDistrictKey(GameClock.YearIndex));

        private static ulong OtherDistrictKey(int yearIndex) => GameSeed.Master ^ (9500UL + (ulong)yearIndex);

        /// <summary>
        /// 自校が居ない各地区を解く処理を組む（地区番号→処理）。出場校・ブロック分けはここ（メインスレッド）で決める
        /// ＝夏の順位など <see cref="GameSession"/> の状態を背景スレッドから読まない。シードは地区ごとに独立。
        /// </summary>
        private static IReadOnlyList<KeyValuePair<int, Func<OtherDistrictResult>>> BuildOtherDistrictJobs(
            int prefectureId, int districtCount, int advance, int yearIndex)
        {
            var jobs = new List<KeyValuePair<int, Func<OtherDistrictResult>>>();
            var myDistrict = ManagerSchool().DistrictId ?? 0;
            var blockLeague = DistrictPlanProvider.IsBlockLeagueOf(prefectureId);
            for (var d = 0; d < districtCount; d++)
            {
                if (d == myDistrict) continue;
                var districtId = d;
                var entrants = NationService.PrefectureSchools.Where(s => s.DistrictId == districtId).ToList();
                var seed = GameSeed.Master ^ (9600UL + (ulong)yearIndex * 10 + (ulong)districtId * 1000
                    + (ulong)prefectureId);
                const string title = "秋季地区予選代表決定";

                // ブロックリーグ戦の県（神奈川・issue #772）: 自校の地区と同じ規則（夏の順位でシード分散・
                // 各ブロック上位 N 校）で解く。
                if (blockLeague)
                {
                    if (entrants.Count < 2)
                    {
                        jobs.Add(Job(districtId, () => new OtherDistrictResult(entrants, Array.Empty<OtherDistrictGame>())));
                        continue;
                    }
                    var blocks = DrawBlocks(entrants, prefectureId, seed);
                    var advancePerBlock = DistrictPlanProvider.AdvancePerBlockOf(prefectureId);
                    jobs.Add(Job(districtId, () => OtherDistrictSim.RunBlockLeague(blocks, advancePerBlock, NationCoeff,
                        new Xoshiro256Random(seed), TournamentEntry.Schedule, title, NewResolver(yearIndex))));
                    continue;
                }

                jobs.Add(Job(districtId, () => OtherDistrictSim.RunKnockout(entrants, advance, NationCoeff,
                    new Xoshiro256Random(seed), TournamentEntry.Schedule, title, NewResolver(yearIndex))));
            }
            return jobs;
        }

        private static KeyValuePair<int, Func<OtherDistrictResult>> Job(int districtId, Func<OtherDistrictResult> run)
            => new KeyValuePair<int, Func<OtherDistrictResult>>(districtId, run);

        /// <summary>
        /// 他地区の裏試合の解決器。自校の地区予選（<see cref="TournamentEntry.Enter"/>）と同じ作り
        /// （県の汎用ティア球場・決勝球場なし）。成績は自前で畳まない＝<see cref="OtherDistrictSim"/> が試合結果を返し、
        /// <see cref="OtherDistrictBackgroundSim.FoldInto"/> が1回だけ積む。
        /// </summary>
        private static BackgroundMatchResolver NewResolver(int yearIndex)
            => new BackgroundMatchResolver(NationService.Rosters,
                new GameContext { Weather = WeatherModel.ForTournament(TournamentEntry.Schedule, TournamentKind.AutumnDistrict) },
                yearIndex, stats: null, modernRules: null,
                calendarYear: SeasonClock.SeasonBaseYear + (yearIndex - 1), brains: new EnemyAiBrainFactory(),
                afterMatch: OtherDistrictBackgroundSim.PauseGate,
                useRegionalStadiumTier: true, summerStadiums: false, prefectureFinalStadiums: false);

        private static School ManagerSchool() => RegionalEntry.ManagerSchool();
    }
}

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
    /// 秋季地区大会（関東大会・九州大会等10地区）への遷移（設計書05 §1.5b）。<see cref="KoshienEntry"/> の
    /// 地区大会版だが、夏と違い<b>県ごとに複数校が出場する</b>（<see cref="RegionalFormat.BerthsFor"/>）ため、
    /// 「優勝したか」ではなく「県内順位が出場枠内か」で自校の出場可否を決める。
    ///
    /// <para><b>暦の週では始まらない。</b> 秋季県大会の後始末（<c>HomeState.DismissResult</c>）から直接つなぐ
    /// （夏の甲子園と同じ理由＝地区大会自体は数試合規模で即座に収束する）。</para>
    ///
    /// <para>自校が出場枠外（または地区大会が単独県開催で県大会がそのまま地区大会）でも、地区大会は
    /// 必ず解決する。自校が出場しない年は<b>観戦もせず同期解決</b>し、そのまま明治神宮大会の判定へ渡す
    /// （地区大会は数試合規模で観戦の意味が薄く、夏の甲子園ほど「聖地」の重みも無いため）。</para>
    /// </summary>
    public static class RegionalEntry
    {
        private static readonly NationCoefficients NationCoeff = new NationCoefficients();
        private const int ManagerSchoolId = -1;

        /// <summary>秋季県大会が終わった直後に呼ぶ。<b>秋以外の大会・自校非対話モードなら何もしない</b>。</summary>
        /// <returns>自校が地区大会（または出場校決定戦）に出場した（＝対話モードで入った）か。</returns>
        public static bool EnterAfterPrefectureTournament(TournamentKind endedKind)
        {
            if (endedKind != TournamentKind.Autumn) return false;
            if (GameSession.Current.Mode != GameMode.Normal) return false;
            return Start(managerPlayoffWon: null);
        }

        /// <summary>
        /// 出場校決定戦（近畿の2026年〜）を自校が戦い終えた直後に呼ぶ。決定戦に勝っていれば地区大会へ対話で入り、
        /// 負けていれば地区大会を観ずに同期で解いて明治神宮の判定へつなぐ。
        /// </summary>
        /// <returns>決定戦の後始末として処理した（＝続けて <see cref="EnterJinguAfterRegionalTournament"/> を呼んではいけない）か。</returns>
        public static bool EnterRegionalAfterPlayoff(TournamentKind endedKind, bool wasPlayoff, bool managerWon)
        {
            if (endedKind != TournamentKind.Regional || !wasPlayoff) return false;
            if (GameSession.Current.Mode != GameMode.Normal) return true;
            Start(managerPlayoffWon: managerWon);
            return true;
        }

        /// <summary>地区大会の出場校決定戦の実体か。決定戦は2校だけの一発勝負で、実際の地区大会は2校にならない。</summary>
        internal static bool IsPlayoff(TournamentKind kind, IReadOnlyCollection<School> field)
            => kind == TournamentKind.Regional && field != null && field.Count == 2;

        private static bool Start(bool? managerPlayoffWon)
        {
            AutumnBackgroundSim.EnsureCompleted();
            BracketFinishSim.EnsureCompleted();
            GameSession.Current.FlushPendingBracketFold();

            var myPlacement = GameSession.Current.LastAutumnPlacementIds;
            if (myPlacement == null || myPlacement.Count == 0) return false;   // 異常系。地区大会を飛ばす。

            var yearIndex = GameClock.YearIndex;
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            var myRegion = PrefectureNames.RegionOf(NationService.PrefectureId);
            var fmt = RegionalFormatProvider.ForRegion(myRegion);
            if (fmt == null) return false;   // 地区データが無い（未整備の地区）は安全側でスキップ。

            if (fmt.SinglePrefecture)
            {
                // 都大会・全道大会が地区大会を兼任（設計書05 §1.5）＝新たな大会を開かない。
                // 県大会の順位がそのまま地区順位＝明治神宮大会の判定へ直接つなぐ。
                return JinguEntry.ContinueFrom(myRegion, myPlacement, yearIndex);
            }

            var myPrefRomaji = PrefectureNames.JisOrder[NationService.PrefectureId];
            var berths = Math.Max(1, fmt.BerthsFor(myPrefRomaji, calendarYear));
            var myRank = IndexOfManager(myPlacement);

            // 出場校決定戦（近畿の2026年〜）の候補＝自校が県大会で決定戦の順位だったとき。まず決定戦を対話で戦う。
            var isPlayoffCandidate = myRank >= 0 && fmt.PlayoffCandidateIndex(myPrefRomaji, calendarYear) == myRank;
            if (isPlayoffCandidate && managerPlayoffWon == null)
            {
                var placements = CollectPlacements(myRegion, fmt, calendarYear, myPlacement, out _);
                var (_, candidates) = RegionalPlayoff.Split(fmt, calendarYear, placements);
                var opponent = PlayoffOpponentOfManager(candidates, yearIndex);
                if (opponent != null) return StartPlayoff(myRegion, yearIndex, opponent);
                managerPlayoffWon = true;   // 組む相手が居ない（候補が奇数・県大会の結果欠け）＝不戦勝で進む
            }

            var qualifies = myRank >= 0 && (myRank < berths || (isPlayoffCandidate && managerPlayoffWon == true));

            var field = BuildField(myRegion, fmt, calendarYear, myPlacement, yearIndex, managerPlayoffWon);
            if (field.Count < 2)
            {
                UnityEngine.Debug.LogWarning(
                    $"[地区大会] {RegionDisplayName(myRegion)}大会の出場校が {field.Count} 校＝開催できない。"
                    + "秋季全国裏試合の結果が揃っていない可能性がある"
                    + $"（他県の消化済み={AutumnBackgroundSim.PrefectureResults.Count} 県）。");
                return false;
            }

            var seed = GameSeed.Master ^ (ulong)(9500 + yearIndex * 10 + (int)TournamentKind.Regional);
            var title = Title(myRegion, yearIndex);

            if (!qualifies)
            {
                // 自校は出場しない＝観戦もせず同期で解決し、そのまま神宮の判定へ渡す。
                var runner = TournamentRunner.Spectated(field, NationCoeff, new Xoshiro256Random(seed),
                    TournamentEntry.Schedule, title);
                runner.DrainBracketResolution();
                var byId = field.ToDictionary(s => s.Id);
                var placement = KnockoutPlacement.FromMatches(runner.BuildBracketView().Matches, id => byId[id])
                    .Select(s => s.Id).ToList();
                return JinguEntry.ContinueFrom(myRegion, placement, yearIndex);
            }

            // 自校が出場枠内＝対話プレイ。
            var manager = ManagerSchool();
            // 今大会スコープをリセットしてから積む（前の秋季県大会の成績を「今大会」に残さない）。
            NationService.TournamentStats.StartTournament();
            var playerRunner = new TournamentRunner(
                field, manager, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule,
                title, new PlayerMatchResolver(), TournamentEntry.BackgroundResolverFor(TournamentKind.Regional, yearIndex),
                onBackgroundMatch: TournamentEntry.FoldBackgroundMatch);
            GameSession.Current.EnterTournament(TournamentKind.Regional, title, playerRunner, field);
            GameSession.Current.Year = yearIndex;
            return true;
        }

        /// <summary>
        /// 自校が地区大会を対話プレイし終えた直後に呼ぶ（<see cref="HomeState.DismissResult"/> から）。
        /// <b>地区大会以外・自校非対話モードなら何もしない</b>。
        /// </summary>
        public static bool EnterJinguAfterRegionalTournament(TournamentKind endedKind)
        {
            if (endedKind != TournamentKind.Regional) return false;
            if (GameSession.Current.Mode != GameMode.Normal) return false;

            // 自校が地区大会を優勝せず敗退したときは、消化しきれなかった残りのカードを
            // BracketFinishSim が背景で解決してから GameSession.FoldTournamentRecords が
            // LastRegionalPlacementIds を確定する（ExitTournamentIfFinished の非同期畳み込み）。
            // ここで先に完了を待たないと、優勝したときだけ通り敗退したときは順位が未確定のまま
            // （count=0）神宮大会・センバツへ繋がらない（2026-08-22 実機検証で発見）。
            BracketFinishSim.EnsureCompleted();
            GameSession.Current.FlushPendingBracketFold();

            var placement = GameSession.Current.LastRegionalPlacementIds;
            if (placement == null || placement.Count == 0) return false;

            // 自校の地区大会成績を焼き取る（設計書05 §1.5b）。次の大会（神宮 or センバツ待ち）で
            // 今大会スコープが消える前に、ここで写す＝これがセンバツの大会展望の
            // 「選手一人一人の地区大会成績」の源（KoshienEntry の県予選焼き取りと同じ作法）。
            GameSession.Current.Stats.SnapshotRegionalQualifier();

            var myRegion = PrefectureNames.RegionOf(NationService.PrefectureId);
            return JinguEntry.ContinueFrom(myRegion, placement, GameClock.YearIndex);
        }

        /// <summary>
        /// 自地区の全出場校（自県は対話プレイ済みの結果、他県は背景シム）を県別出場枠ぶんだけ集める。
        /// 出場校決定戦のある地区・年は、決定戦の勝者も加える。自校が決定戦の候補のときは
        /// <paramref name="managerPlayoffWon"/> に自校の勝敗を渡す（自校の組は対話で戦った結果を使う）。
        /// </summary>
        internal static List<School> BuildField(
            string region, RegionalFormat fmt, int calendarYear, IReadOnlyList<int> myPlacement,
            int yearIndex, bool? managerPlayoffWon = null)
        {
            var placements = CollectPlacements(region, fmt, calendarYear, myPlacement, out var missing);
            var (field, candidates) = RegionalPlayoff.Split(fmt, calendarYear, placements);
            field.AddRange(ResolvePlayoffWinners(candidates, yearIndex, managerPlayoffWon));
            if (missing.Count > 0)
                UnityEngine.Debug.LogWarning(
                    $"[地区大会] {RegionDisplayName(region)}大会の出場枠を埋められない県がある: "
                    + string.Join("・", missing) + $"（出場校 {field.Count} 校で開催してしまう）。"
                    + "秋季全国裏試合（AutumnBackgroundSim）が回っていない可能性がある。");
            return field;
        }

        /// <summary>県ごとの県大会順位（直接枠と決定戦の候補の順位まで）を県Id順に集める。結果の無い県は <paramref name="missing"/> へ。</summary>
        private static List<(string Pref, IReadOnlyList<School> Placement)> CollectPlacements(
            string region, RegionalFormat fmt, int calendarYear, IReadOnlyList<int> myPlacement, out List<string> missing)
        {
            var myPrefId = NationService.PrefectureId;
            var otherResults = AutumnBackgroundSim.PrefectureResults;
            var placements = new List<(string, IReadOnlyList<School>)>();

            missing = new List<string>();
            foreach (var prefId in PrefecturesInRegion(region))
            {
                IReadOnlyList<int> placement = prefId == myPrefId
                    ? myPlacement
                    : otherResults.FirstOrDefault(r => r.PrefectureId == prefId)?.PlacementIds
                      ?? Array.Empty<int>();
                // 県大会の結果が無い県は出場枠を埋められない。ここを黙って飛ばすと、地区大会が
                // 「結果のある県ぶんだけ」の小さな大会として成立してしまい、絵の上では正常に見える
                // （2040年秋＝自県2校だけの九州大会。issue autumn-regional-field-collapses-after-load）。
                if (placement.Count == 0) { missing.Add(PrefectureNames.JapaneseOf(prefId)); continue; }

                var prefRomaji = PrefectureNames.JisOrder[prefId];
                var need = Math.Max(Math.Max(1, fmt.BerthsFor(prefRomaji, calendarYear)),
                    (fmt.PlayoffCandidateIndex(prefRomaji, calendarYear) ?? -1) + 1);
                var schools = new List<School>();
                foreach (var id in placement.Take(need))
                {
                    var school = id == ManagerSchoolId ? ManagerSchool() : NationService.SchoolById(id);
                    if (school != null) schools.Add(school);
                }
                placements.Add((prefRomaji, schools));
            }
            return placements;
        }

        private static ulong PlayoffSeed(int yearIndex)
            => GameSeed.Master ^ (ulong)(9800 + yearIndex * 10 + (int)TournamentKind.Regional);

        /// <summary>自校の決定戦の相手。自校が不戦勝（相手が居ない）なら null。</summary>
        private static School PlayoffOpponentOfManager(IReadOnlyList<School> candidates, int yearIndex)
        {
            foreach (var (a, b) in RegionalPlayoff.Pair(candidates, new Xoshiro256Random(PlayoffSeed(yearIndex))))
            {
                if (b == null) continue;
                if (a.Id == ManagerSchoolId) return b;
                if (b.Id == ManagerSchoolId) return a;
            }
            return null;
        }

        /// <summary>
        /// 決定戦の勝者（組の順）。自校の組は <paramref name="managerPlayoffWon"/> の結果を使い、残りの組は集計モデルで解く
        /// （組ごとに別ストリーム＝自校の組を対話で戦っても他の組の結果は変わらない）。
        /// </summary>
        private static List<School> ResolvePlayoffWinners(
            IReadOnlyList<School> candidates, int yearIndex, bool? managerPlayoffWon)
        {
            var rng = new Xoshiro256Random(PlayoffSeed(yearIndex));
            var winners = new List<School>();
            var pairs = RegionalPlayoff.Pair(candidates, rng);
            for (var i = 0; i < pairs.Count; i++)
            {
                var (a, b) = pairs[i];
                if (b == null) { winners.Add(a); continue; }
                var managerIsA = a.Id == ManagerSchoolId;
                if (managerIsA || b.Id == ManagerSchoolId)
                {
                    if (managerPlayoffWon == null)
                        throw new InvalidOperationException("自校が出場校決定戦の候補なのに、決定戦の勝敗が渡されていない。");
                    var manager = managerIsA ? a : b;
                    var other = managerIsA ? b : a;
                    winners.Add(managerPlayoffWon == true ? manager : other);
                    continue;
                }
                winners.Add(RegionalPlayoff.Play(a, b, i, NationCoeff, rng));
            }
            return winners;
        }

        /// <summary>自校が出場校決定戦（自校と相手の2校の一発勝負）へ対話で入る。</summary>
        private static bool StartPlayoff(string region, int yearIndex, School opponent)
        {
            var manager = ManagerSchool();
            var field = new List<School> { manager, opponent };
            var title = Title(region, yearIndex) + " 出場校決定戦";
            var seed = PlayoffSeed(yearIndex) ^ 0x1UL;
            NationService.TournamentStats.StartTournament();
            var runner = new TournamentRunner(
                field, manager, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule,
                title, new PlayerMatchResolver(), TournamentEntry.BackgroundResolverFor(TournamentKind.Regional, yearIndex),
                onBackgroundMatch: TournamentEntry.FoldBackgroundMatch);
            GameSession.Current.EnterTournament(TournamentKind.Regional, title, runner, field);
            GameSession.Current.Year = yearIndex;
            return true;
        }

        /// <summary>順位付き出場校ID列の中で自校(-1)が何位（0基点）か。居なければ-1。</summary>
        private static int IndexOfManager(IReadOnlyList<int> placement)
        {
            for (var i = 0; i < placement.Count; i++) if (placement[i] == ManagerSchoolId) return i;
            return -1;
        }

        internal static IEnumerable<int> PrefecturesInRegion(string region)
            => Enumerable.Range(0, PrefectureNames.JisOrder.Count).Where(p => PrefectureNames.RegionOf(p) == region);

        internal static School ManagerSchool()
        {
            var trainable = new List<KokoSim.Engine.Season.DevelopingPlayer>();
            foreach (var p in RosterService.Active) if (p.Grade <= 3) trainable.Add(p);
            return new School
            {
                Id = ManagerSchoolId,
                Name = NationService.ManagerSchoolName,
                PrefectureId = NationService.PrefectureId,
                DistrictId = NationService.DistrictId,
                Strength = trainable.Count == 0 ? 40.0 : TeamOverall.Of(trainable),
            };
        }

        internal static string Title(string region, int yearIndex)
        {
            var year = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            return year + "年 秋季" + RegionDisplayName(region) + "大会";
        }

        /// <summary>地区名（ローマ字）→表示名。<see cref="RegionalFormatSet"/> は表示名を持たないため、
        /// 実在の地区大会名の慣習（"秋季関東大会"等）に合わせてここで対訳する。</summary>
        internal static string RegionDisplayName(string regionRomaji) => regionRomaji switch
        {
            "hokkaido" => "北海道",
            "tohoku" => "東北",
            "kanto" => "関東",
            "tokyo" => "東京都",
            "hokushinetsu" => "北信越",
            "tokai" => "東海",
            "kinki" => "近畿",
            "chugoku" => "中国",
            "shikoku" => "四国",
            "kyushu" => "九州",
            _ => regionRomaji,
        };
    }
}

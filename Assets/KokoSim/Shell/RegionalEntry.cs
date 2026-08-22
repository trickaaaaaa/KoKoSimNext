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
        /// <returns>自校が地区大会に出場した（＝対話モードで地区大会に入った）か。</returns>
        public static bool EnterAfterPrefectureTournament(TournamentKind endedKind)
        {
            if (endedKind != TournamentKind.Autumn) return false;
            if (GameSession.Current.Mode != GameMode.Normal) return false;

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
            var qualifies = myRank >= 0 && myRank < berths;

            var field = BuildField(myRegion, fmt, calendarYear, myPlacement);
            if (field.Count < 2) return false;   // 出場校が1校以下＝大会が成立しない（異常系）。

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
            var playerRunner = new TournamentRunner(
                field, manager, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule,
                title, new PlayerMatchResolver(), BackgroundResolver(yearIndex));
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

            var myRegion = PrefectureNames.RegionOf(NationService.PrefectureId);
            return JinguEntry.ContinueFrom(myRegion, placement, GameClock.YearIndex);
        }

        /// <summary>
        /// 自地区の全出場校（自県は対話プレイ済みの結果、他県は背景シム）を県別出場枠ぶんだけ集める。
        /// </summary>
        internal static List<School> BuildField(
            string region, RegionalFormat fmt, int calendarYear, IReadOnlyList<int> myPlacement)
        {
            var myPrefId = NationService.PrefectureId;
            var otherResults = AutumnBackgroundSim.PrefectureResults;
            var field = new List<School>();

            foreach (var prefId in PrefecturesInRegion(region))
            {
                IReadOnlyList<int> placement = prefId == myPrefId
                    ? myPlacement
                    : otherResults.FirstOrDefault(r => r.PrefectureId == prefId)?.PlacementIds
                      ?? Array.Empty<int>();
                if (placement.Count == 0) continue;

                var prefRomaji = PrefectureNames.JisOrder[prefId];
                var berths = Math.Max(1, fmt.BerthsFor(prefRomaji, calendarYear));
                foreach (var id in placement.Take(berths))
                {
                    var school = id == ManagerSchoolId ? ManagerSchool() : NationService.SchoolById(id);
                    if (school != null) field.Add(school);
                }
            }
            return field;
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

        internal static IBackgroundMatchResolver BackgroundResolver(int yearIndex)
        {
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            return new MemoizingBackgroundResolver(
                new BackgroundMatchResolver(NationService.Rosters, new GameContext(), yearIndex, stats: null,
                    modernRules: null, calendarYear: calendarYear, brains: new EnemyAiBrainFactory()));
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

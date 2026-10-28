using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 秋季地区予選（神奈川4地区・兵庫5地区・愛知5地区・北海道10支部等、地理固定割の県だけ）から
    /// 秋季県大会（<see cref="TournamentKind.Autumn"/>）への遷移。<see cref="KoshienEntry"/>／
    /// <see cref="RegionalEntry"/> と同型のシェル層クラス（issue autumn-district-tournament-not-wired）。
    ///
    /// <para><b>暦の週では始まらない。</b> 秋季地区予選の後始末（<c>HomeState.DismissResult</c>）から
    /// 直接つなぐ（夏の甲子園と同じ理由＝暦の開幕週を作らない）。</para>
    ///
    /// <para>自校の地区は <see cref="TournamentEntry"/> が対話プレイで消化する（1つの
    /// <see cref="TournamentRunner"/>）。同じ県の他地区は数校規模のブラケットなので専用の背景シムを
    /// 新設せず、<see cref="TournamentRunner.Spectated"/>＋<see cref="TournamentRunner.DrainBracketResolution"/>
    /// でその場・同期に解決する（<see cref="RegionalEntry"/> が「自校が出場枠外」のときに使うのと
    /// 同じパターンの再利用。全国46〜48県ぶんを回す <see cref="AutumnBackgroundSim"/> ほどの規模ではない）。</para>
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
            var field = CollectDistrictQualifiers(prefectureId, districtCount, advance, myQualifierIds, yearIndex);
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

            var runner = new TournamentRunner(field, manager, NationCoeff, new Xoshiro256Random(seed),
                TournamentEntry.Schedule, title, new PlayerMatchResolver(), RegionalEntry.BackgroundResolver(yearIndex),
                onBackgroundMatch: (awayId, homeId, g) => NationService.TournamentStats.FoldMatch(awayId, homeId, g));
            GameSession.Current.EnterTournament(TournamentKind.Autumn, title, runner, field);
            GameSession.Current.Year = yearIndex;
            return true;
        }

        /// <summary>
        /// 自校の地区の進出校＋他地区の進出校（同期解決）を集める。district 0..districtCount-1 の並びで
        /// 決定論（他地区の解決順が結果に影響しないよう、シードは地区番号ごとに分ける）。
        /// </summary>
        private static List<School> CollectDistrictQualifiers(
            int prefectureId, int districtCount, int advance, IReadOnlyList<int> myQualifierIds, int yearIndex)
        {
            var manager = ManagerSchool();
            var field = new List<School>(districtCount * advance);
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

                field.AddRange(ResolveOtherDistrict(prefectureId, d, advance, yearIndex));
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

        /// <summary>自校が居ない地区の進出校（上位 advance 校）を同期解決する（自校IDを混ぜないので Spectated でよい）。</summary>
        private static IReadOnlyList<School> ResolveOtherDistrict(
            int prefectureId, int districtId, int advance, int yearIndex)
        {
            var entrants = NationService.PrefectureSchools.Where(s => s.DistrictId == districtId).ToList();
            if (entrants.Count <= advance) return entrants;

            var seed = GameSeed.Master ^ (9600UL + (ulong)yearIndex * 10 + (ulong)districtId * 1000
                + (ulong)prefectureId);
            var title = "秋季地区予選代表決定";
            var runner = TournamentRunner.Spectated(
                entrants, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule, title);
            runner.DrainBracketResolution();

            var byId = entrants.ToDictionary(s => s.Id);
            return KnockoutPlacement.FromMatches(runner.BuildBracketView().Matches, id => byId[id])
                .Take(advance).ToList();
        }

        private static School ManagerSchool() => RegionalEntry.ManagerSchool();
    }
}

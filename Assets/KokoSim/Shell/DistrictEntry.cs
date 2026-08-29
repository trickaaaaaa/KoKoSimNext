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
            var yearIndex = GameClock.YearIndex;
            var field = CollectDistrictChampions(prefectureId, districtCount, myChampionId.Value, yearIndex);
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
            var runner = new TournamentRunner(field, manager, NationCoeff, new Xoshiro256Random(seed),
                TournamentEntry.Schedule, title, new PlayerMatchResolver(), RegionalEntry.BackgroundResolver(yearIndex),
                onBackgroundMatch: (awayId, homeId, g) => NationService.TournamentStats.FoldMatch(awayId, homeId, g));
            GameSession.Current.EnterTournament(TournamentKind.Autumn, title, runner, field);
            GameSession.Current.Year = yearIndex;
            return true;
        }

        /// <summary>
        /// 自校の地区代表＋他地区の代表（同期解決）を集める。district 0..districtCount-1 の並びで
        /// 決定論（他地区の解決順が結果に影響しないよう、シードは地区番号ごとに分ける）。
        /// </summary>
        private static List<School> CollectDistrictChampions(
            int prefectureId, int districtCount, int myChampionId, int yearIndex)
        {
            var manager = ManagerSchool();
            var field = new List<School>(districtCount);
            for (var d = 0; d < districtCount; d++)
            {
                if (d == (manager.DistrictId ?? 0))
                {
                    var mine = myChampionId == ManagerSchoolId ? manager : NationService.SchoolById(myChampionId);
                    if (mine != null) field.Add(mine);
                    continue;
                }

                var champion = ResolveOtherDistrict(prefectureId, d, yearIndex);
                if (champion != null) field.Add(champion);
            }
            return field;
        }

        /// <summary>自校が居ない地区の代表を同期解決する（自校IDを混ぜないので Spectated でよい）。</summary>
        private static School ResolveOtherDistrict(int prefectureId, int districtId, int yearIndex)
        {
            var entrants = NationService.PrefectureSchools.Where(s => s.DistrictId == districtId).ToList();
            if (entrants.Count == 0) return null;
            if (entrants.Count == 1) return entrants[0];

            var seed = GameSeed.Master ^ (9600UL + (ulong)yearIndex * 10 + (ulong)districtId * 1000
                + (ulong)prefectureId);
            var title = "秋季地区予選代表決定";
            var runner = TournamentRunner.Spectated(
                entrants, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule, title);
            runner.DrainBracketResolution();

            var byId = entrants.ToDictionary(s => s.Id);
            var placement = KnockoutPlacement.FromMatches(runner.BuildBracketView().Matches, id => byId[id]);
            return placement.Count > 0 ? placement[0] : null;
        }

        private static School ManagerSchool() => RegionalEntry.ManagerSchool();
    }
}

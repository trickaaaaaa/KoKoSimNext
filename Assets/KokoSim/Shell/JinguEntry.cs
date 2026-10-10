using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 明治神宮大会（高校の部）への遷移（設計書05 §1.5b）。地区大会10校ぶんの王者によるトーナメント。
    /// 地区大会の後始末から直接つなぐ（暦の週を持たない・<see cref="RegionalEntry"/> と同じ理由）。
    /// </summary>
    public static class JinguEntry
    {
        private static readonly NationCoefficients NationCoeff = new NationCoefficients();
        private const int ManagerSchoolId = -1;

        /// <summary>
        /// 自地区の順位付き出場校ID（<paramref name="myRegionPlacement"/>、先頭が地区王者）から神宮大会へ
        /// つなぐ。<see cref="RegionalEntry"/> の対話/同期どちらの経路からも呼ばれる共通の入口。
        /// </summary>
        /// <returns>自校が神宮大会に出場した（＝対話モードで神宮大会に入った）か。</returns>
        public static bool ContinueFrom(string myRegion, IReadOnlyList<int> myRegionPlacement, int yearIndex)
        {
            if (myRegionPlacement == null || myRegionPlacement.Count == 0) return false;
            var myRegionChampionId = myRegionPlacement[0];

            var otherRegions = ComputeOtherRegions(myRegion, yearIndex);

            var field = new List<School>();
            var myChampion = myRegionChampionId == ManagerSchoolId
                ? RegionalEntry.ManagerSchool() : NationService.SchoolById(myRegionChampionId);
            if (myChampion == null) return false;
            field.Add(myChampion);
            foreach (var r in otherRegions)
            {
                if (!r.ChampionToJingu || !r.HasChampion) continue;
                var s = NationService.SchoolById(r.ChampionId);
                if (s != null) field.Add(s);
            }
            if (field.Count < 2)
            {
                // 他9地区の王者が1校も居ない＝地区大会が組めていない（＝47県の秋季県大会が空）。
                // ここで黙って返すと明治神宮大会もセンバツ選考も丸ごと飛ぶ
                // （issue autumn-regional-field-collapses-after-load）。
                UnityEngine.Debug.LogWarning(
                    $"[明治神宮] 出場校が {field.Count} 校＝開催できない。神宮大会とセンバツ選考を飛ばす"
                    + $"（他地区の王者={otherRegions.Count(r => r.ChampionToJingu && r.HasChampion)} 校）。");
                return false;
            }

            var seed = GameSeed.Master ^ (ulong)(9600 + yearIndex * 10 + (int)TournamentKind.Jingu);
            var title = Title(yearIndex);
            var championIsManager = myRegionChampionId == ManagerSchoolId;

            if (championIsManager)
            {
                var manager = RegionalEntry.ManagerSchool();
                var runner = new TournamentRunner(
                    field, manager, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule,
                    title, new PlayerMatchResolver(), RegionalEntry.BackgroundResolver(yearIndex));
                GameSession.Current.EnterTournament(TournamentKind.Jingu, title, runner, field);
                GameSession.Current.Year = yearIndex;
                return true;
            }

            var spectated = TournamentRunner.Spectated(field, NationCoeff, new Xoshiro256Random(seed),
                TournamentEntry.Schedule, title);
            spectated.DrainBracketResolution();
            var byId = field.ToDictionary(s => s.Id);
            var jinguChampionId = KnockoutPlacement.FromMatches(spectated.BuildBracketView().Matches, id => byId[id])
                .Select(s => s.Id).FirstOrDefault();
            var jinguChampionRegion = RegionOfChampion(jinguChampionId, myRegion, myRegionChampionId, otherRegions);
            SenbatsuEntry.ContinueFrom(myRegion, myRegionPlacement, otherRegions, jinguChampionRegion, yearIndex);
            return false;
        }

        /// <summary>
        /// 自校が神宮大会を対話プレイし終えた直後に呼ぶ（<see cref="HomeState.DismissResult"/> から）。
        /// <b>神宮大会以外・自校非対話モードなら何もしない</b>。
        /// </summary>
        public static bool EnterSenbatsuAfterJinguTournament(TournamentKind endedKind)
        {
            if (endedKind != TournamentKind.Jingu) return false;
            if (GameSession.Current.Mode != GameMode.Normal) return false;

            // 自校が神宮大会で優勝せず敗退したときは LastJinguChampionId がまだ背景畳み込み待ち
            // （RegionalEntry.EnterJinguAfterRegionalTournament と同じ理由・2026-08-22 実機検証で発見）。
            // 先に完了させないと ?? PrefectureResult.NoChampion に落ちて「神宮に王者が居ない」扱いになる。
            BracketFinishSim.EnsureCompleted();
            GameSession.Current.FlushPendingBracketFold();

            var myPlacement = GameSession.Current.MyRegionPlacementIds;
            if (myPlacement == null || myPlacement.Count == 0) return false;
            var myRegion = PrefectureNames.RegionOf(NationService.PrefectureId);

            var otherRegions = ComputeOtherRegions(myRegion, GameClock.YearIndex);
            var jinguChampionId = GameSession.Current.LastJinguChampionId ?? PrefectureResult.NoChampion;
            var jinguChampionRegion = RegionOfChampion(jinguChampionId, myRegion, myPlacement[0], otherRegions);
            SenbatsuEntry.ContinueFrom(myRegion, myPlacement, otherRegions, jinguChampionRegion, GameClock.YearIndex);
            return false;
        }

        private static string RegionOfChampion(
            int championId, string myRegion, int myRegionChampionId, IReadOnlyList<RegionalResult> otherRegions)
        {
            if (championId == myRegionChampionId) return myRegion;
            foreach (var r in otherRegions) if (r.ChampionId == championId) return r.Region;
            return myRegion;   // 想定外（データ欠如）は自地区へ倒す＝神宮枠の付与を諦めない
        }

        /// <summary>自地区以外の9地区の地区大会を解決する（<see cref="RegionalNationEngine.RunOtherRegions"/>）。
        /// 自県は対話プレイ済み（<c>GameSession.LastAutumnPlacementIds</c>）、他県は背景シムの結果を使う。</summary>
        private static IReadOnlyList<RegionalResult> ComputeOtherRegions(string myRegion, int yearIndex)
        {
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            var placements = new Dictionary<int, IReadOnlyList<int>>();
            var myPrefId = NationService.PrefectureId;
            var myPlacement = GameSession.Current.LastAutumnPlacementIds;
            if (myPlacement != null) placements[myPrefId] = myPlacement;
            foreach (var r in AutumnBackgroundSim.PrefectureResults) placements[r.PrefectureId] = r.PlacementIds;

            var seed = GameSeed.Master ^ (ulong)(9550 + yearIndex * 10);
            return RegionalNationEngine.RunOtherRegions(
                RegionalFormatProvider.Set, placements, PrefectureNames.RegionOf,
                id => id == ManagerSchoolId ? RegionalEntry.ManagerSchool() : NationService.SchoolById(id),
                NationCoeff, new Xoshiro256Random(seed), calendarYear, excludeRegion: myRegion);
        }

        private static string Title(int yearIndex)
            => (SeasonClock.SeasonBaseYear + (yearIndex - 1)) + "年 明治神宮野球大会（高校の部）";
    }
}

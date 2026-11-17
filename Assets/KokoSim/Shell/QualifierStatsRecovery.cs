using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ロード直後に、甲子園・センバツの代表校の「県予選／地区大会成績」の積み残しを背景で積み直す（issue #1116）。
    /// 積む背景（<see cref="KoshienQualifierStatsSim"/> / <see cref="SenbatsuQualifierStatsSim"/>）は進捗を
    /// 保存しないので、積み終える前に保存されたセーブ（修正前のセーブを含む）は一部の校の成績が空のまま残る。
    ///
    /// <para>積み直しは「すでに積んだ校は飛ばす」作りなので二重には積まれない。足りない校が無ければ何もせず、
    /// 通常のロードには待ちを足さない。足りない校があるときだけ、保存されない全国裏試合の結果
    /// （夏の代表の勝ち上がり／他地区の地区大会ブラケット）を同じシードで再計算して待つ。</para>
    /// </summary>
    public static class QualifierStatsRecovery
    {
        private const int ManagerSchoolId = -1;

        public static void ResumeAfterLoad(SaveState s)
        {
            var koshien = FieldOf(s, TournamentKind.SummerKoshien, s.Koshien);
            if (koshien != null) ResumeKoshien(koshien);

            var senbatsu = FieldOf(s, TournamentKind.SenbatsuKoshien, s.Senbatsu)
                ?? PendingSenbatsuField();
            if (senbatsu != null) ResumeSenbatsu(senbatsu);
        }

        private static IReadOnlyList<int> FieldOf(SaveState s, TournamentKind kind, TournamentSaveState spectated)
        {
            if (s.Tournament != null && s.Tournament.Kind == kind) return s.Tournament.FieldSchoolIds;
            return spectated?.FieldSchoolIds;
        }

        private static IReadOnlyList<int> PendingSenbatsuField()
        {
            var ids = GameSession.Current.PendingSenbatsuFieldIds;
            return ids != null && ids.Count > 0 ? ids : null;
        }

        private static void ResumeKoshien(IReadOnlyList<int> fieldIds)
        {
            var stats = NationService.TournamentStats;
            var homeId = GameSession.Current.LastPrefChampionId;
            var missing = false;
            foreach (var id in fieldIds)
            {
                if (id == ManagerSchoolId || id == homeId) continue;   // 自県の代表は実測（スナップショット）を持つ
                if (!HasStats(stats.PrefQualifierForSchool(id))) { missing = true; break; }
            }
            if (!missing) return;

            var yearIndex = GameClock.YearIndex;
            TournamentEntry.EnsureSummerBackgroundResults(yearIndex);
            KoshienQualifierStatsSim.Start(NationBackgroundSim.PrefectureResults, yearIndex, GameSeed.Master);
        }

        private static void ResumeSenbatsu(IReadOnlyList<int> fieldIds)
        {
            var stats = NationService.TournamentStats;
            var myRegion = PrefectureNames.RegionOf(NationService.PrefectureId);
            var missing = false;
            foreach (var id in fieldIds)
            {
                if (id == ManagerSchoolId) continue;
                if (HasStats(stats.RegionalQualifierForSchool(id))) continue;
                // 自地区の他校は積む手立てが無い（SenbatsuQualifierStatsSim の「対象外」）＝毎回の待ちを生まないよう数えない。
                var school = NationService.SchoolById(id);
                if (school == null || PrefectureNames.RegionOf(school.PrefectureId) == myRegion) continue;
                missing = true;
                break;
            }
            if (!missing) return;

            var yearIndex = GameClock.YearIndex;
            TournamentEntry.EnsureAutumnBackgroundResults(yearIndex);
            var otherRegions = JinguEntry.ComputeOtherRegions(myRegion, yearIndex);
            SenbatsuQualifierStatsSim.Start(fieldIds, otherRegions, yearIndex, GameSeed.Master, keepExisting: true);
        }

        private static bool HasStats(StatBook book) => book != null && book.Players.Count > 0;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 春のセンバツ本戦（32代表校の全国大会）への遷移（設計書05 §1.5b）。<see cref="KoshienEntry"/> の
    /// センバツ版で、作り込みは夏の甲子園と同格にする（N年ぶりN回目・専用イベント・大会展望）。
    ///
    /// <para>神宮大会が終わった瞬間（<see cref="JinguEntry"/> の即座連鎖の末尾）に32校は確定済みで、
    /// <see cref="ContinueFrom"/> がそれを <see cref="GameSession.PendingSenbatsuFieldIds"/> へ控えるだけ。
    /// 実際の大会モードへの遷移（<see cref="Announce"/>）は選考発表週
    /// （<see cref="SeasonCalendar.SenbatsuAnnounceWeek"/>）まで待つ＝神宮大会（11月頃）と選考発表（1月）の
    /// 間に現実で冬合宿・新入生確定等の実プレイ週が挟まるため（夏の甲子園と違う点）。</para>
    /// </summary>
    public static class SenbatsuEntry
    {
        private static readonly NationCoefficients NationCoeff = new NationCoefficients();
        private const int ManagerSchoolId = -1;

        /// <summary>
        /// 地区大会・明治神宮大会が終わった時点でセンバツ32校を確定し控える。乱数は引かない
        /// （<see cref="SenbatsuSelection.Select"/> は既に決まった順位から機械的に選ぶだけ）。
        /// </summary>
        public static void ContinueFrom(
            string myRegion, IReadOnlyList<int> myRegionPlacement,
            IReadOnlyList<RegionalResult> otherRegions, string jinguChampionRegion, int yearIndex)
        {
            var placements = new Dictionary<string, IReadOnlyList<School>>
            {
                [myRegion] = ResolveSchools(myRegionPlacement),
            };
            foreach (var r in otherRegions) placements[r.Region] = ResolveSchools(r.PlacementIds);

            var field = SenbatsuSelection.Select(placements, new SenbatsuBerths(), jinguChampionRegion);
            var fieldIds = field.Select(s => s.Id).ToList();
            GameSession.Current.SetPendingSenbatsuField(fieldIds, jinguChampionRegion);

            // 選出校の地区大会成績を背景で打ち直す（設計書05 §1.5b）。選考発表までに終わっていれば
            // 「選手一人一人の地区大会成績」が実測で埋まる。終わっていなくても進行は止まらない（読み物なので）。
            SenbatsuQualifierStatsSim.Start(fieldIds, otherRegions, yearIndex, GameSeed.Master);
        }

        /// <summary>
        /// 選考発表週に呼ぶ（<see cref="GameClock"/> の週送りフックから）。控えていた32校を読み上げ、
        /// 自校が選ばれていれば対話モードへ、選ばれていなければ観るだけのセンバツへ入る。
        /// </summary>
        public static void Announce()
        {
            var fieldIds = GameSession.Current.PendingSenbatsuFieldIds;
            if (fieldIds == null || fieldIds.Count == 0) return;   // 神宮大会が一度も走っていない（新規ゲーム等）

            var yearIndex = GameClock.YearIndex;
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            RecordAppearances(fieldIds, calendarYear);

            var field = new List<School>(fieldIds.Count);
            foreach (var id in fieldIds)
            {
                var s = id == ManagerSchoolId ? RegionalEntry.ManagerSchool() : NationService.SchoolById(id);
                if (s != null) field.Add(s);
            }
            var jinguRegion = GameSession.Current.PendingSenbatsuJinguRegion;
            GameSession.Current.SetPendingSenbatsuField(new List<int>(), "");   // 発表済み＝消費する

            var selected = fieldIds.Contains(ManagerSchoolId);
            var title = Title(yearIndex);
            var seed = GameSeed.Master ^ (ulong)(9700 + yearIndex * 10 + (int)TournamentKind.SenbatsuKoshien);

            if (!selected)
            {
                EventService.Push("senbatsu_not_selected", subjectName: null,
                    PresentedEffect.Bad("今年は選外だった。"));
                var spectated = BuildSpectatedRunner(field, title, seed, yearIndex);
                GameSession.Current.EnterSpectatedSenbatsu(title, spectated, field);
                return;
            }

            var runner = BuildPlayerRunner(field, title, seed, yearIndex);
            PushBerthDonation();
            PushDraw(runner, jinguRegion);
            GameSession.Current.EnterTournament(TournamentKind.SenbatsuKoshien, title, runner, field);
            GameSession.Current.Year = yearIndex;
        }

        /// <summary>セーブから観るだけのセンバツへ復帰する（<see cref="TournamentEntry.RestoreRunner"/> のセンバツ版）。</summary>
        public static void RestoreSpectated(TournamentSaveState t)
        {
            if (t == null) return;
            var field = new List<School>(t.FieldSchoolIds.Count);
            foreach (var id in t.FieldSchoolIds)
            {
                var s = NationService.SchoolById(id);
                if (s == null) throw new InvalidOperationException($"セーブのセンバツ出場校 Id={id} が全国に見つかりません。");
                field.Add(s);
            }
            var runner = TournamentRunner.FromSaved(
                t.Runner, field, RegionalEntry.ManagerSchool(), NationCoeff, TournamentEntry.Schedule,
                playerResolver: null, backgroundResolver: BackgroundResolver(GameClock.YearIndex),
                onBackgroundMatch: FoldBackgroundMatch);
            GameSession.Current.RestoreSpectatedSenbatsu(t, runner, field);
        }

        public static string Title(int yearIndex)
            => (SeasonClock.SeasonBaseYear + (yearIndex - 1)) + "年 選抜高等学校野球大会";

        private static List<School> ResolveSchools(IReadOnlyList<int> ids)
        {
            var list = new List<School>(ids.Count);
            foreach (var id in ids)
            {
                var s = id == ManagerSchoolId ? RegionalEntry.ManagerSchool() : NationService.SchoolById(id);
                if (s != null) list.Add(s);
            }
            return list;
        }

        private static void RecordAppearances(IReadOnlyList<int> fieldIds, int calendarYear)
        {
            foreach (var id in fieldIds)
                GameSession.Current.Records.RecordKoshienAppearance(id, KoshienKind.Spring, calendarYear);
        }

        private static IBackgroundMatchResolver BackgroundResolver(int yearIndex)
        {
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            var ctx = new GameContext { Field = MatchStadium.For(TournamentKind.SenbatsuKoshien) };
            return new MemoizingBackgroundResolver(
                new BackgroundMatchResolver(NationService.Rosters, ctx, yearIndex, stats: null,
                    modernRules: null, calendarYear: calendarYear, brains: new EnemyAiBrainFactory()));
        }

        private static void FoldBackgroundMatch(int awayId, int homeId, GameResult g)
            => NationService.TournamentStats.FoldMatch(awayId, homeId, g);

        private static TournamentRunner BuildPlayerRunner(
            IReadOnlyList<School> field, string title, ulong seed, int yearIndex)
        {
            NationService.TournamentStats.StartTournament();
            return new TournamentRunner(
                field, RegionalEntry.ManagerSchool(), NationCoeff, new Xoshiro256Random(seed),
                TournamentEntry.Schedule, title, new PlayerMatchResolver(), BackgroundResolver(yearIndex),
                isNationalTournament: true, onBackgroundMatch: FoldBackgroundMatch);
        }

        private static TournamentRunner BuildSpectatedRunner(
            IReadOnlyList<School> field, string title, ulong seed, int yearIndex)
        {
            NationService.TournamentStats.StartTournament();
            return TournamentRunner.Spectated(
                field, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule, title,
                BackgroundResolver(yearIndex), isNationalTournament: true, onBackgroundMatch: FoldBackgroundMatch);
        }

        private static void PushBerthDonation()
        {
            var c = ManagerService.Coefficients;
            var m = ManagerService.Manager;
            var amount = c.KoshienBerthDonationBase + c.KoshienBerthDonationPerFame * m.Fame;

            var before = m.Funds;
            m.Funds += amount;
            var moved = m.Funds - before;

            EventService.Push("senbatsu_berth_donation", subjectName: null,
                PresentedEffect.Good("部費 ＋" + Math.Round(moved).ToString("0") + " 万！"));
        }

        private static void PushDraw(TournamentRunner runner, string jinguRegion)
        {
            var opponent = runner.NextOpponent;
            var sentence = opponent == null
                ? "初戦は不戦勝。2回戦から登場する。"
                : "初戦の相手は " + opponent.Name + "（"
                  + TournamentDate.Label(TournamentEntry.Schedule, TournamentKind.SenbatsuKoshien, runner.NextMatchDay)
                  + "）に決まった。";
            EventService.Push("senbatsu_draw", subjectName: null, PresentedEffect.Good(sentence));
        }
    }
}

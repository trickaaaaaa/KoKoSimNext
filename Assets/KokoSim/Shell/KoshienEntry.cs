using System.Collections.Generic;
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
    /// 夏の甲子園（49代表の全国大会）への遷移（設計書05 §1.1a）。<see cref="TournamentEntry"/> の甲子園版。
    ///
    /// <para><b>暦の週では始まらない。</b> 夏の県大会の後始末（<c>HomeState.DismissResult</c>）から直接つなぐ。
    /// 甲子園を第18週あたりの開幕週にすると、3年引退（<see cref="SeasonCalendar.SummerRetireWeek"/>+1＝第18週）と
    /// 夏合宿（第18週）に正面衝突して<b>出場校の3年が甲子園の前に引退する</b>。県大会の直後につなげば、
    /// 甲子園が消費した週ぶんの時計送りで第18週を通過する＝「3年は甲子園を戦ってから引退する」が自然に成立し、
    /// <see cref="SeasonCalendar"/> の定数は1つも触らずに済む（成長段階・合宿・引退の判定は不変＝統計帯に無影響）。</para>
    ///
    /// <para>自校が出場する年は対話モード（<see cref="GameSession.EnterTournament"/>）、
    /// 出場しない年は観るだけの進行体（<see cref="GameSession.EnterSpectatedKoshien"/>）になる。
    /// どちらの年も甲子園は必ず開催され、優勝校まで決まる（オーナー決定 2026-08-21）。</para>
    /// </summary>
    public static class KoshienEntry
    {
        private static readonly NationCoefficients NationCoeff = new NationCoefficients();
        private const int ManagerSchoolId = -1;

        /// <summary>
        /// 夏の県大会が終わった直後に呼ぶ。49代表を集めて甲子園を開き、自校が出場するなら
        /// 激励金・抽選会のイベントを積む。<b>夏以外の大会・既に甲子園が開催中なら何もしない</b>。
        /// </summary>
        /// <returns>自校が出場した（＝対話モードで甲子園に入った）か。</returns>
        public static bool EnterAfterPrefectureTournament(TournamentKind endedKind)
        {
            if (endedKind != TournamentKind.Summer) return false;
            if (GameSession.Current.Mode != GameMode.Normal) return false;
            if (GameSession.Current.HasSpectatedKoshien) return false;

            // 49代表が揃うのを待つ。他県は背景シム、自県は敗退後の残りブラケット背景消化が持っている。
            // EnsureCompleted はスレッドの完了を待つだけで、完了通知（＝自県の畳み込み）はメインスレッドの
            // キューに積まれたまま走っていない＝待った直後に自分で確定させる（FlushPendingBracketFold）。
            NationBackgroundSim.EnsureCompleted();
            BracketFinishSim.EnsureCompleted();
            GameSession.Current.FlushPendingBracketFold();

            // 県予選成績を焼き取る（設計書05 §1.4b）。甲子園の開始で今大会スコープは消えるので、
            // <b>消える前に</b>ここで写す＝これが甲子園の大会展望の「選手一人一人の県予選成績」の源。
            // 残りブラケットの背景消化が終わった後でないと、自校が早期敗退した年の他校ぶんが欠ける。
            GameSession.Current.Stats.SnapshotPrefQualifier();
            NationService.TournamentStats.SnapshotPrefQualifier();

            var results = NationBackgroundSim.PrefectureResults;
            var reps = CollectRepresentatives(results);
            if (reps.Count < 2)
            {
                // 49代表が揃わない。全国裏試合（NationBackgroundSim）の結果はセーブに載らない static なので、
                // Play を起動し直してから夏の県大会中のセーブをロードすると空になる
                // （issue summer-koshien-skipped-after-load）。黙って飛ばすと「甲子園が来ない年」に見える。
                UnityEngine.Debug.LogWarning(
                    $"[甲子園] 代表が {reps.Count} 校＝開催できない。今年の甲子園を飛ばす"
                    + $"（消化済みの県={results.Count} 区画）。");
                return false;
            }

            var yearIndex = GameClock.YearIndex;
            RecordAppearances(reps, yearIndex);

            // 他県代表の県予選成績を背景で打ち直す（設計書05 §1.4b）。展望を開くまでに終わっていれば
            // 「選手一人一人の県予選成績」が実測で埋まる。終わっていなくても進行は止まらない（読み物なので）。
            KoshienQualifierStatsSim.Start(results, yearIndex, GameSeed.Master);

            var managerIsIn = false;
            foreach (var s in reps) if (s.Id == ManagerSchoolId) { managerIsIn = true; break; }

            var title = Title(yearIndex);
            var seed = GameSeed.Master ^ (ulong)(9000 + yearIndex * 10 + (int)TournamentKind.SummerKoshien);

            if (!managerIsIn)
            {
                GameSession.Current.EnterSpectatedKoshien(title, BuildSpectatedRunner(reps, title, seed, yearIndex), reps);
                return false;
            }

            // イベントは大会モードへ入る**前**に積む。開幕バナーが出る週はノベルを開かない作りなので、
            // 後から積むと会話がバナーの裏に隠れて次の機会まで出てこない（HomeDashboardController.OnAdvance）。
            var runner = BuildPlayerRunner(reps, title, seed, yearIndex);
            PushBerthDonation();
            PushDraw(runner);

            GameSession.Current.EnterTournament(TournamentKind.SummerKoshien, title, runner, reps);
            GameSession.Current.Year = yearIndex;
            return true;
        }

        /// <summary>
        /// セーブから観るだけの甲子園へ復帰する（<see cref="TournamentEntry.RestoreRunner"/> の甲子園版）。
        /// 自校が出場している甲子園は通常の大会モードとして <see cref="TournamentEntry.RestoreRunner"/> が戻す。
        /// </summary>
        public static void RestoreSpectated(TournamentSaveState t)
        {
            if (t == null) return;
            var field = new List<School>(t.FieldSchoolIds.Count);
            foreach (var id in t.FieldSchoolIds)
            {
                var s = NationService.SchoolById(id);
                // 自校（Id=-1）は観るだけの甲子園には居ない。見つからない Id は黙って落とさず投げる。
                if (s == null) throw new System.InvalidOperationException($"セーブの甲子園出場校 Id={id} が全国に見つかりません。");
                field.Add(s);
            }

            var runner = TournamentRunner.FromSaved(
                t.Runner, field, ManagerSchool(), NationCoeff, TournamentEntry.Schedule,
                playerResolver: null, backgroundResolver: BackgroundResolver(GameClock.YearIndex),
                onBackgroundMatch: FoldBackgroundMatch);
            GameSession.Current.RestoreSpectatedKoshien(t, runner, field);
        }

        /// <summary>大会名（"2028年 全国高等学校野球選手権大会"）。県大会の "2028年 選手権神奈川大会" と同じ作法。</summary>
        public static string Title(int yearIndex)
            => (SeasonClock.SeasonBaseYear + (yearIndex - 1)) + "年 全国高等学校野球選手権大会";

        // ===== 出場校 =====

        /// <summary>
        /// 49代表を集める。自県は直前の県大会の優勝校（<see cref="GameSession.LastPrefChampionId"/>）、
        /// 他県は背景シムが返した区画ごとの優勝校。<b>区画Id順に整列してから返す</b>
        /// ＝背景の並列消化は順不同なので、並び順に依存させない（決定論・不変条件#2）。
        ///
        /// <para>自県が北海道・東京（分割県）でも49代表になる。<c>TournamentEntry.BuildField</c> が
        /// 自校の区画（<see cref="SummerRegions.RegionFor"/>）だけで県大会を組み、もう片方の区画は
        /// このメソッドが読む背景シムが独自の代表を出すため（issue 北海道・東京分割）。</para>
        /// </summary>
        private static List<School> CollectRepresentatives(IReadOnlyList<PrefectureResult> source)
        {
            var results = new List<PrefectureResult>(source);
            results.Sort((a, b) =>
            {
                var c = a.PrefectureId.CompareTo(b.PrefectureId);
                return c != 0 ? c : string.CompareOrdinal(a.PrefectureName, b.PrefectureName);
            });

            var reps = new List<School>(49);
            var seen = new HashSet<int>();
            foreach (var r in results)
            {
                if (!r.HasChampion) continue;
                var school = NationService.SchoolById(r.ChampionId);
                if (school != null && seen.Add(school.Id)) reps.Add(school);
            }

            // 自県の代表（自校が優勝していれば自校）。背景シムは自県を除外しているのでここで足す。
            if (GameSession.Current.LastPrefChampionId is int homeId && seen.Add(homeId))
                reps.Add(homeId == ManagerSchoolId ? ManagerSchool() : NationService.SchoolById(homeId));
            reps.RemoveAll(s => s == null);
            return reps;
        }

        /// <summary>自校（共有ロスターの今の強さで組む）。<see cref="TournamentEntry"/> と同じ作法。</summary>
        private static School ManagerSchool()
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

        /// <summary>
        /// 49代表全員の甲子園出場を記録する（「N年ぶりN回目」＝<see cref="AppearanceLabel"/> の材料）。
        ///
        /// <para><b>自県の代表はここで数えない</b>＝県大会の畳み込み（<c>SchoolRecordBook.FoldTournament</c>）が
        /// もう1回積んでいる。ここで積み直すと同じ夏を2回出場したことになる。</para>
        /// </summary>
        private static void RecordAppearances(IReadOnlyList<School> reps, int yearIndex)
        {
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            var homeId = GameSession.Current.LastPrefChampionId;
            foreach (var s in reps)
            {
                if (homeId is int id && s.Id == id) continue;
                GameSession.Current.Records.RecordKoshienAppearance(s.Id, KoshienKind.Summer, calendarYear);
            }
        }

        // ===== 進行体 =====

        private static IBackgroundMatchResolver BackgroundResolver(int yearIndex)
        {
            var calendarYear = SeasonClock.SeasonBaseYear + (yearIndex - 1);
            // 甲子園の裏カードも1球フルシムで解く（49校＝48試合なので県大会1つぶんより軽い）。
            // メモ化で包むのは自校が出る年だけでよいが、両方同じ形にして経路を1本に保つ。
            // 球場は自校戦と同じ甲子園（設計書13 §1）＝同じ大会の中で球場が2種類にならない。
            var ctx = new GameContext { Field = MatchStadium.For(TournamentKind.SummerKoshien) };
            return new MemoizingBackgroundResolver(
                new BackgroundMatchResolver(NationService.Rosters, ctx, yearIndex, stats: null,
                    modernRules: null, calendarYear: calendarYear, brains: new EnemyAiBrainFactory()));
        }

        /// <summary>裏カードの成績を全国の今大会スコープへ積む（自校戦は自校ストアが持つ＝帰属先は1つ）。</summary>
        private static void FoldBackgroundMatch(int awayId, int homeId, GameResult g)
            => NationService.TournamentStats.FoldMatch(awayId, homeId, g);

        private static TournamentRunner BuildPlayerRunner(
            IReadOnlyList<School> reps, string title, ulong seed, int yearIndex)
        {
            // 今大会スコープをリセットしてから積み直す（県予選ぶんは既に県予選スコープへ焼き取り済み）。
            NationService.TournamentStats.StartTournament();
            return new TournamentRunner(
                reps, ManagerSchool(), NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule,
                title, new PlayerMatchResolver(), BackgroundResolver(yearIndex),
                // 甲子園本戦はコールドなし（設計書05 §1.3。地方大会の決勝と同じ扱い）。
                isNationalTournament: true,
                onBackgroundMatch: FoldBackgroundMatch);
        }

        private static TournamentRunner BuildSpectatedRunner(
            IReadOnlyList<School> reps, string title, ulong seed, int yearIndex)
        {
            NationService.TournamentStats.StartTournament();
            return TournamentRunner.Spectated(
                reps, NationCoeff, new Xoshiro256Random(seed), TournamentEntry.Schedule, title,
                BackgroundResolver(yearIndex), isNationalTournament: true, onBackgroundMatch: FoldBackgroundMatch);
        }

        // ===== イベント（設計書05 §1.1a・issue 甲子園） =====

        /// <summary>
        /// OB・卒業生からの激励金（部費＝遠征・宿泊・応援バスに使う運営費）。
        /// 金額は係数から組み、<b>実際に動いた量</b>で結果行を書く（<c>EventService.ApplyEffect</c> と同じ作法）。
        /// </summary>
        private static void PushBerthDonation()
        {
            var c = ManagerService.Coefficients;
            var m = ManagerService.Manager;
            var amount = c.KoshienBerthDonationBase + c.KoshienBerthDonationPerFame * m.Fame;

            var before = m.Funds;
            m.Funds += amount;
            var moved = m.Funds - before;

            EventService.Push("koshien_berth_donation", subjectName: null,
                PresentedEffect.Good("部費 ＋" + System.Math.Round(moved).ToString("0") + " 万！"));
        }

        /// <summary>
        /// 組み合わせ抽選会。結果行に初戦の相手と試合日を出す（設計書05 §1.2「抽選会はイベントとして演出」）。
        /// 抽選そのものは <see cref="TournamentDraw.Arrange"/> が進行体の生成時に済ませている
        /// ＝ここは<b>決まったことを読み上げるだけ</b>で、乱数を引き直さない。
        /// </summary>
        private static void PushDraw(TournamentRunner runner)
        {
            var opponent = runner.NextOpponent;
            var sentence = opponent == null
                // 初戦不戦勝（49校＝64枠なので上位シードに起こりうる）。
                ? "初戦は不戦勝。2回戦から登場する。"
                : "初戦の相手は " + opponent.Name + "（"
                  + TournamentDate.Label(TournamentEntry.Schedule, TournamentKind.SummerKoshien, runner.NextMatchDay)
                  + "）に決まった。";
            EventService.Push("koshien_draw", subjectName: null, PresentedEffect.Good(sentence));
        }
    }
}

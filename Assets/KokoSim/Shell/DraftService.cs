using System.Collections.Generic;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 週次評価で予想バンドが動いた1件（設計書20 §5）。差分検出は Shell の責務で、
    /// engine はその週のスナップショットを返すだけ。<b>セーブには載せない</b>（表示のための前回比較で、
    /// 進行の実体ではない）。
    /// </summary>
    public sealed class DraftBandChange
    {
        public int PlayerId;
        public string Name;
        public DraftRankBand From;
        public DraftRankBand To;

        /// <summary>圏外から候補入りした（＝新しく名前が挙がった）。</summary>
        public bool Entered { get { return From == DraftRankBand.None; } }

        /// <summary>評価が上がった。</summary>
        public bool Improved { get { return To > From; } }
    }

    /// <summary>
    /// ドラフト（設計書20）の Unity 側の入口（issue #586）。engine の <see cref="DraftEngine"/> は
    /// #178 で実装済みだったが Unity から一度も呼ばれておらず、指名が起きないため教え子名鑑に出す
    /// 実績が存在しなかった。ここで週送りへ接続する。
    ///
    /// <para><b>帯不変</b>（設計書20 §7）: 注目度は純関数で乱数を使わず、指名だけが母種から導く
    /// 独立ストリームを引く（<see cref="NominationStream"/>）。本編の乱数列は1ビットも進まない。</para>
    ///
    /// <para><b>評価の母数は公式戦通算</b>（<see cref="PlayerStatStore.Official"/>）。練習試合の成績は
    /// 公になった実績ではないのでスカウトの評価軸に載せない。</para>
    /// </summary>
    public static class DraftService
    {
        /// <summary>指名抽選の独立ストリーム識別子（<see cref="GameSeed.Master"/> と混合）。</summary>
        private const ulong NominationStream = 0x5586_0000UL;

        /// <summary>係数（C#既定＝data/draft.yaml と同値）。</summary>
        public static DraftCoefficients Coefficients { get; private set; } = new DraftCoefficients();

        // 候補入りの足切り（設計書20 §3.1a）は選手画面のカテゴリランクと同じ合成で判定する
        // ＝チップを出している画面群と同じ単一ソース（TeamStrengthCoeff.Default）を渡す。
        private static KokoSim.Engine.Nation.TeamStrengthCoefficients Strength { get { return TeamStrengthCoeff.Default; } }

        private static DraftHistory _history;

        // 前回週のバンド（選手Id→バンド）。null＝まだ基準を採っていない＝次の評価は差分を出さない。
        private static Dictionary<int, DraftRankBand> _bands;

        private static readonly List<DraftBandChange> _changes = new List<DraftBandChange>();

        // 指名の確定の入口は2つ（週入り／大会の日送り）。どちらから来ても同じ年度で二度確定しないための歯止め
        // （issue #1731）。大会の途中でセーブしても読み込み後に二重にならないよう、最後に確定した年度はセーブに載せる。
        private static readonly DraftNominationGate Gate =
            new DraftNominationGate(new SeasonCalendar(), TournamentEntry.Schedule);

        /// <summary>教え子名鑑（過年度の指名確定）。セーブ v7 で永続化する。</summary>
        public static DraftHistory History
        {
            get { return _history ?? (_history = new DraftHistory()); }
        }

        /// <summary>未消費のバンド変化（ホームが通知フィードへ流したら <see cref="DrainChanges"/> で空にする）。</summary>
        public static IReadOnlyList<DraftBandChange> PendingChanges { get { return _changes; } }

        /// <summary>直前の指名週の結果（ホームが流したら null へ戻す）。指名漏れもここには残る。</summary>
        public static DraftResult LastNomination { get; set; }

        /// <summary>
        /// 評価対象＝卒業前の在籍者（<see cref="RosterService.Roster"/>）。<see cref="RosterService.Active"/>
        /// ではない: 3年生は夏に引退して Active から外れるが、指名は10月最終週なので<b>引退した3年こそが本命</b>。
        /// </summary>
        private static IReadOnlyList<DevelopingPlayer> Pool { get { return RosterService.Roster; } }

        private static PlayerStats StatsOf(int playerId)
        {
            return GameSession.Current.Stats.Official.Get(playerId);
        }

        /// <summary>
        /// 「当時の背番号」（0＝背番号なし）。指名は10月で、3年生は<b>夏の引退で背番号を返上済み</b>
        /// （issue #531）＝選手本体を見ても必ず0になる。大会別アーカイブを遡って最後に付けていた番号を採る。
        /// </summary>
        private static int UniformNumberOf(int playerId)
        {
            foreach (var p in Pool)
                if (p.Id == playerId && p.UniformNumber > 0) return p.UniformNumber;
            return GameSession.Current.Stats.Archive.LastUniformNumber(playerId);
        }

        /// <summary>今の在籍者ぜんぶの注目度スナップショット（ドラフト画面・キャリア画面の表示用）。</summary>
        public static IReadOnlyList<DraftEvaluation> Evaluate()
        {
            return DraftEngine.EvaluateRoster(Pool, StatsOf, Coefficients, Strength);
        }

        /// <summary>
        /// 週送りのたびに評価し直し、予想バンドが動いた選手を控える（設計書20 §5）。
        /// 初回（基準が無いとき）は差分を出さずに基準だけ採る＝セーブ復元直後に
        /// 「候補入り」が全員ぶん暴発しない。
        /// </summary>
        public static void RefreshBands()
        {
            var next = new Dictionary<int, DraftRankBand>();
            var baseline = _bands;
            foreach (var e in Evaluate())
            {
                next[e.PlayerId] = e.Band;
                if (baseline == null) continue;

                DraftRankBand prev;
                if (!baseline.TryGetValue(e.PlayerId, out prev)) prev = DraftRankBand.None;
                if (prev == e.Band) continue;

                _changes.Add(new DraftBandChange
                {
                    PlayerId = e.PlayerId,
                    Name = e.Name,
                    From = prev,
                    To = e.Band,
                });
            }
            _bands = next;
        }

        /// <summary>控えていたバンド変化を取り出して空にする（ホームの通知フィードが消費する）。</summary>
        public static IReadOnlyList<DraftBandChange> DrainChanges()
        {
            if (_changes.Count == 0) return System.Array.Empty<DraftBandChange>();
            var taken = new List<DraftBandChange>(_changes);
            _changes.Clear();
            return taken;
        }

        /// <summary>直近の指名で入ったOBの一時金（指名1人ごと・issue #1469）。ホームが通知フィードへ流したら空にする。</summary>
        public static IReadOnlyList<(DraftPick Pick, double Amount)> LastOBDonations { get; set; } =
            System.Array.Empty<(DraftPick, double)>();

        /// <summary>
        /// 共有週が週に入った。ドラフトの週（10月4週目）なら指名を確定して場面を積む（二度目の年度は何もしない）。
        /// 通常の週送りも、大会の後始末でまとめて進む週送りも、1週ずつここを通る（<see cref="GameClock"/> の週入りフック）。
        /// </summary>
        public static void OnWeekEntered(int yearIndex, int week, int calendarYear)
        {
            if (Gate.TryClaimOnWeekEntered(yearIndex, week)) Nominate(yearIndex, calendarYear);
        }

        /// <summary>
        /// 大会モードの日が進んだ。大会の実月日が10月4週目に入った日なら、その日のうちに指名を確定して場面を積む
        /// （大会の間は共有週が止まっているので、週入りだけだと大会が終わるまで指名が来ない。issue #1731）。
        /// 後始末で共有週が第28週を通るときは、確定済みなので何もしない。
        /// </summary>
        public static void OnTournamentDay(TournamentKind kind, int tournamentDay)
        {
            var yearIndex = GameClock.YearIndex;
            if (Gate.TryClaimOnTournamentDay(yearIndex, kind, tournamentDay))
                Nominate(yearIndex, GameClock.DraftYear());
        }

        /// <summary>
        /// 指名の確定（設計書20 §3.2）。対象は3年生の候補だけで、指名された者を教え子名鑑へ積み、
        /// 結果（指名漏れも含む）を通知フィード用に残し、ドラフトの場面を<b>1つだけ</b>積む。
        /// </summary>
        private static void Nominate(int yearIndex, int calendarYear)
        {
            var rng = new Xoshiro256Random(GameSeed.Master ^ (NominationStream ^ (ulong)yearIndex));
            var result = DraftEngine.RunNomination(yearIndex, Pool, StatsOf, Coefficients, Strength, rng);
            History.AddFrom(result, calendarYear, UniformNumberOf);

            LastNomination = result;
            // プロ入りOBの一時金（issue #1469 O2）。指名を確定したその週に部費へ入れる。会話は別に積まず、場面の結果の行へ入れる。
            LastOBDonations = ManagerService.ApplyDraftNominations(result);
            RaiseScene(result, LastOBDonations);
        }

        /// <summary>
        /// ドラフトの会話を1場面で積む（issue #1731）。指名が何人いても場面は1つ。結果の行は指名・指名漏れの全員ぶん＋祝い金。
        /// 上限を通さない（落ちると三年間見てきた選手の最後が通知1行で終わる）。
        /// </summary>
        internal static void RaiseScene(DraftResult result, IReadOnlyList<(DraftPick Pick, double Amount)> donations)
        {
            var content = DraftScene.Build(result, donations);
            var effect = content.ResultSentence.Length == 0
                ? null
                : content.ResultIsGood
                    ? PresentedEffect.Good(content.ResultSentence)
                    : PresentedEffect.Bad(content.ResultSentence);
            EventService.PushUncapped(content.EventId, content.LeadName, effect, result.Year, content.Values);
        }

        /// <summary>セーブ保存: 教え子名鑑を DTO へ写す（issue #586）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            s.Draft = History;
            s.DraftNominatedYearIndex = Gate.LastYearIndex;
        }

        /// <summary>
        /// セーブ復元: 教え子名鑑を据え、バンドの基準を採り直す（<see cref="RosterService"/> の後に呼ぶこと）。
        /// 基準を採り直さないと、読み込み直後の1週目に在籍候補が全員「候補入り」として流れる。
        /// </summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s)
        {
            _history = s.Draft ?? new DraftHistory();
            _bands = null;
            _changes.Clear();
            Gate.LastYearIndex = s.DraftNominatedYearIndex;
            LastNomination = null;
            LastOBDonations = System.Array.Empty<(DraftPick, double)>();
            RefreshBands();   // 基準だけ採る（_bands が null なので差分は出ない）
        }

        /// <summary>新規ゲームの完全リセット。</summary>
        public static void Reset()
        {
            _history = null;
            _bands = null;
            _changes.Clear();
            Gate.LastYearIndex = 0;
            LastNomination = null;
            LastOBDonations = System.Array.Empty<(DraftPick, double)>();
        }
    }
}

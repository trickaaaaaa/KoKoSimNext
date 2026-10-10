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

        // 同じ年度で二度指名しないためのガード（0＝未実施）。セーブには載せない
        // ＝指名週は年に一度しか入らないので、復元後に再入場することがない。
        private static int _lastNominatedYear;

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

        /// <summary>
        /// 10月最終週の指名確定（設計書20 §3.2）。対象は3年生の候補だけで、指名された者を
        /// 教え子名鑑へ積む。返り値には指名漏れも含む（通知フィードが「指名されなかった」を出すため）。
        /// 同じ年度で二度呼ばれたら null を返す。
        /// </summary>
        public static DraftResult RunNomination(int yearIndex, int calendarYear)
        {
            if (yearIndex <= _lastNominatedYear) return null;

            var rng = new Xoshiro256Random(GameSeed.Master ^ (NominationStream ^ (ulong)yearIndex));
            var result = DraftEngine.RunNomination(yearIndex, Pool, StatsOf, Coefficients, Strength, rng);
            History.AddFrom(result, calendarYear, UniformNumberOf);

            _lastNominatedYear = yearIndex;
            LastNomination = result;
            return result;
        }

        /// <summary>セーブ保存: 教え子名鑑を DTO へ写す（issue #586）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            s.Draft = History;
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
            _lastNominatedYear = 0;
            LastNomination = null;
            RefreshBands();   // 基準だけ採る（_bands が null なので差分は出ない）
        }

        /// <summary>新規ゲームの完全リセット。</summary>
        public static void Reset()
        {
            _history = null;
            _bands = null;
            _changes.Clear();
            _lastNominatedYear = 0;
            LastNomination = null;
        }
    }
}

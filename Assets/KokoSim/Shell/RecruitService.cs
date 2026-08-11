using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>訪問の結果（画面へ返す理由付きの返事）。</summary>
    public enum VisitResult
    {
        /// <summary>訪問できた（争奪戦は起きなかった）。</summary>
        Visited,
        /// <summary>訪問できたが、競合校が動いた（争奪戦）。</summary>
        VisitedWithRivalMove,
        /// <summary>いまは訪問できない時期（秋〜冬の外）。</summary>
        OutOfSeason,
        /// <summary>勧誘対象に指定していない（枠を割り当てていない）。</summary>
        NotTargeted,
        /// <summary>部費が足りない。</summary>
        NotEnoughFunds,
    }

    /// <summary>
    /// 能動勧誘の単一ソース（設計書04 §2.2・issue #588）。寮を建てると年最大2名の枠が解禁され、
    /// 秋に出る中学生リストから対象を選び、秋〜冬に訪問を重ね、<b>3月の確定週に成否が決まる</b>。
    /// 成功した中学生はそのまま入部予定者（<see cref="ProspectService"/>）へ合流する。
    ///
    /// <para>乱数は<b>受動枠とは別のストリーム</b>で引く（不変条件#2）＝勧誘を何回試しても
    /// 毎年の新入生は1ビットも動かない。ロジックの実体は engine の <see cref="RecruitModel"/>。</para>
    /// </summary>
    public static class RecruitService
    {
        /// <summary>中学生リストの生成に使う決定論ストリーム。</summary>
        private const ulong ListStream = 0x5588_0000UL;
        /// <summary>訪問（争奪戦の抽選）に使うストリーム。訪問回数が変わっても他へ波及しない。</summary>
        private const ulong VisitStream = 0x5589_0000UL;
        /// <summary>3月の成否判定に使うストリーム。</summary>
        private const ulong ResolveStream = 0x558A_0000UL;

        private static readonly RecruitCoefficients Coeff = new RecruitCoefficients();
        private static readonly ProspectScoutingCoefficients ScoutCoeff = new ProspectScoutingCoefficients();
        private static readonly TeamStrengthCoefficients StrengthCoeff = new TeamStrengthCoefficients();
        private static readonly SeasonCalendar Calendar = new SeasonCalendar();

        // 訪問の抽選を回すたびに進む連番。同じ週に何度訪問しても同じ引きにならないようにする。
        private static int _visitCounter;

        /// <summary>いま抱えている勧誘の状態。null＝寮が無い／まだ秋を跨いでいない。</summary>
        public static RecruitingState Current { get; private set; }

        /// <summary>解禁されている枠数（寮の段から。0＝勧誘できない）。</summary>
        public static int Slots
            => ManagerService.Catalog.AggregateEffects(ManagerService.Facilities).RecruitSlots;

        /// <summary>いま訪問できる時期か（秋〜冬）。</summary>
        public static bool InSeason => Calendar.IsRecruitingSeason(GameClock.Week);

        /// <summary>係数（画面が費用を出すために読む）。</summary>
        public static RecruitCoefficients Coefficients => Coeff;

        /// <summary>
        /// 秋のリスト生成フック（<see cref="SeasonCalendar.IsRecruitListWeek"/> の週に呼ぶ）。
        /// 寮が無ければ何もしない。同じ年度ぶんを作り直さない＝冪等。
        /// </summary>
        /// <param name="joinYearIndex">合流する年度（＝入部予定者と同じ年度）。</param>
        /// <returns>この呼び出しで新たにリストが出たら true。</returns>
        public static bool BuildList(int joinYearIndex)
        {
            if (Slots <= 0) return false;
            if (Current != null && Current.YearIndex == joinYearIndex) return false;

            var rng = new Xoshiro256Random(GameSeed.Master ^ (ListStream ^ (ulong)joinYearIndex));
            var state = RecruitModel.BuildList(
                joinYearIndex, Slots, NationService.PrefectureId, HomeTier(),
                RosterService.TalentCenter(), new RosterCoefficients(), Coeff, rng);

            // 見立ては受動枠と同じ仕組み（スカウト網の段で精度が変わる）。生成とは別ストリームで引く。
            var scoutRng = new Xoshiro256Random(GameSeed.Master ^ ((ListStream | 0x1UL) ^ (ulong)joinYearIndex));
            var disclosure = ManagerService.ContractEffects.ProspectDisclosure;
            foreach (var p in state.Prospects)
                p.Report = ProspectScouting.Survey(p.Player, disclosure, StrengthCoeff, ScoutCoeff, scoutRng);

            Current = state;
            _visitCounter = 0;
            return true;
        }

        /// <summary>
        /// 勧誘対象の指定を切り替える。枠を超えては指定できない（＝寮の段が「年最大何名」を決める）。
        /// </summary>
        /// <returns>指定を変更できたら true。</returns>
        public static bool ToggleTarget(int index)
        {
            var p = At(index);
            if (p == null || Current.Resolved) return false;
            if (p.Targeted) { p.Targeted = false; return true; }
            if (Current.TargetedCount >= Current.Slots) return false;
            p.Targeted = true;
            return true;
        }

        /// <summary>
        /// 1回訪問する。距離に応じた費用を部費から引き、確率で争奪戦を起こす（設計書04 §2.2）。
        /// 資金の判定と引き落としは<b>ここだけ</b>が持つ（engine は金庫を持たない）。
        /// </summary>
        public static VisitResult Visit(int index)
        {
            var p = At(index);
            if (p == null || Current.Resolved) return VisitResult.OutOfSeason;
            if (!InSeason) return VisitResult.OutOfSeason;
            if (!p.Targeted) return VisitResult.NotTargeted;

            var cost = RecruitModel.VisitCost(p.Distance, Coeff);
            if (ManagerService.Manager.Funds < cost) return VisitResult.NotEnoughFunds;
            ManagerService.Manager.Funds -= cost;

            var rng = new Xoshiro256Random(GameSeed.Master ^ (VisitStream ^ (ulong)(_visitCounter++)));
            return RecruitModel.Visit(p, Coeff, rng)
                ? VisitResult.VisitedWithRivalMove
                : VisitResult.Visited;
        }

        /// <summary>いまの成功率（画面がそのまま出す）。</summary>
        public static double SuccessProbability(int index)
        {
            var p = At(index);
            return p == null
                ? 0.0
                : RecruitModel.SuccessProbability(p, ManagerService.Manager.Fame, HomeTier(), HasDorm, Coeff);
        }

        /// <summary>訪問1回の費用[万円]。</summary>
        public static double VisitCost(int index)
        {
            var p = At(index);
            return p == null ? 0.0 : RecruitModel.VisitCost(p.Distance, Coeff);
        }

        /// <summary>
        /// 3月の成否判定（入部予定者の確定と同じ週）。成功した中学生を<b>そのまま</b>入部予定者へ合流させる。
        /// <see cref="ProspectService.Confirm"/> の<b>後</b>に呼ぶこと（受動枠の器ができてから足す）。
        /// </summary>
        /// <returns>勧誘に成功した人数。</returns>
        public static int Resolve(int joinYearIndex)
        {
            if (Current == null || Current.Resolved || Current.YearIndex != joinYearIndex) return 0;

            var rng = new Xoshiro256Random(GameSeed.Master ^ (ResolveStream ^ (ulong)joinYearIndex));
            var won = RecruitModel.Resolve(
                Current, ManagerService.Manager.Fame, HomeTier(), HasDorm, Coeff, rng);

            foreach (var p in won) ProspectService.AddRecruited(p.Player, p.Report);
            return won.Count;
        }

        /// <summary>寮を持っているか（距離の減点が和らぐ）。</summary>
        public static bool HasDorm => ManagerService.Facilities.LevelOf("dorm") > 0;

        /// <summary>自校の格（競合校の格と比べる基準）。</summary>
        private static Tier HomeTier() => Tiers.FromStrength(TeamOverall.Of(RosterService.Active));

        private static RecruitProspect At(int index)
            => Current != null && index >= 0 && index < Current.Prospects.Count ? Current.Prospects[index] : null;

        /// <summary>セーブ保存。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s) { s.Recruiting = Current; }

        /// <summary>セーブ復元（v5 以前は null＝勧誘を持たない進行）。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s) { Current = s.Recruiting; _visitCounter = 0; }

        /// <summary>新規ゲームの完全リセット。</summary>
        public static void Reset() { Current = null; _visitCounter = 0; }
    }
}

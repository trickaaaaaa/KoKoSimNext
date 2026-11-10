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
        /// <summary>枠が埋まっている（他の対象を外さないと追加できない）。</summary>
        SlotsFull,
        /// <summary>部費が足りない。</summary>
        NotEnoughFunds,
    }

    /// <summary>
    /// スカウト（設計書04 §2.4・issue #867）の単一ソース。年契約「スカウト網」の段が枠数と見立ての段を兼ね、
    /// 秋に候補リストが出て、秋〜冬に訪問を重ね、<b>3月の確定週に成否が決まる</b>。
    /// 成功した候補は受動枠の自動の子と差し替わる（<see cref="ProspectService.AbsorbScouted"/>）。
    ///
    /// <para>乱数は<b>受動枠とは別のストリーム</b>で引く（不変条件#2）＝スカウトを何回試しても
    /// 毎年の新入生は1ビットも動かない。ロジックの実体は engine の <see cref="ScoutModel"/>。</para>
    ///
    /// <para>旧 <c>RecruitService</c>（寮で解禁の能動勧誘・issue #588）を置き換える。</para>
    /// </summary>
    public static class ScoutService
    {
        private const ulong ListStream = 0x5588_0000UL;
        private const ulong VisitStream = 0x5589_0000UL;
        private const ulong ResolveStream = 0x558A_0000UL;

        private static readonly ScoutCoefficients Coeff = new ScoutCoefficients();
        private static readonly TeamStrengthCoefficients StrengthCoeff = new TeamStrengthCoefficients();
        private static readonly SeasonCalendar Calendar = new SeasonCalendar();

        private static int _visitCounter;

        /// <summary>いま抱えているスカウトの状態。null＝未契約／まだ秋を跨いでいない。</summary>
        public static ScoutingState Current { get; private set; }

        /// <summary>探す分野（次のリスト生成に使う。いつでも変えられる＝生成時に凍結）。</summary>
        public static ScoutField SelectedField { get; set; } = ScoutField.Omakase;

        /// <summary>解禁されている枠数（スカウト網の契約段から。0＝スカウトできない）。</summary>
        public static int Slots => ManagerService.ContractEffects.ScoutSlots;

        /// <summary>スカウト網の契約段（0〜3）。</summary>
        public static int Level => ManagerService.Contracts.LevelOf("scouting");

        /// <summary>いま訪問できる時期か（秋〜冬）。</summary>
        public static bool InSeason => Calendar.IsRecruitingSeason(GameClock.Week);

        /// <summary>係数（画面が費用を出すために読む）。</summary>
        public static ScoutCoefficients Coefficients => Coeff;

        /// <summary>
        /// 秋のリスト生成フック（<see cref="SeasonCalendar.IsRecruitListWeek"/> の週に呼ぶ）。
        /// 未契約（枠0）なら何もしない。同じ年度ぶんを作り直さない＝冪等。
        /// </summary>
        /// <param name="joinYearIndex">合流する年度（＝入部予定者と同じ年度）。</param>
        /// <returns>この呼び出しで新たにリストが出たら true。</returns>
        public static bool BuildList(int joinYearIndex)
        {
            var slots = Slots;
            if (slots <= 0) return false;
            if (Current != null && Current.YearIndex == joinYearIndex) return false;

            var rng = new Xoshiro256Random(GameSeed.Master ^ (ListStream ^ (ulong)joinYearIndex));
            Current = ScoutModel.BuildCandidates(
                joinYearIndex, SelectedField, Level, slots, NationService.PrefectureId, HomeTier(),
                RosterService.TalentCenter(), ManagerService.ContractEffects.ScoutTalentAdd,
                new RosterCoefficients(), Coeff, StrengthCoeff, rng);
            _visitCounter = 0;
            return true;
        }

        /// <summary>
        /// 1回訪問する。まだ対象にしていなければ、枠に空きがある限り<b>自動で対象にする</b>
        /// （画面のボタンは1つだけ・オーナー決定のワイヤー案A）。距離に応じた費用を部費から引き、
        /// 確率で争奪戦を起こす。資金の判定と引き落としは<b>ここだけ</b>が持つ（engine は金庫を持たない）。
        /// </summary>
        public static VisitResult Visit(int index)
        {
            var p = At(index);
            if (p == null || Current.Resolved || !InSeason) return VisitResult.OutOfSeason;

            if (!p.Targeted)
            {
                if (Current.TargetedCount >= Current.Slots) return VisitResult.SlotsFull;
                p.Targeted = true;
            }

            var cost = ScoutModel.VisitCost(p.Distance, Coeff);
            if (ManagerService.Manager.Funds < cost) return VisitResult.NotEnoughFunds;
            ManagerService.Manager.Funds -= cost;

            var rng = new Xoshiro256Random(GameSeed.Master ^ (VisitStream ^ (ulong)(_visitCounter++)));
            return ScoutModel.Visit(p, Coeff, rng) ? VisitResult.VisitedWithRivalMove : VisitResult.Visited;
        }

        /// <summary>訪問1回の費用[万円]。</summary>
        public static double VisitCost(int index)
        {
            var p = At(index);
            return p == null ? 0.0 : ScoutModel.VisitCost(p.Distance, Coeff);
        }

        /// <summary>
        /// 3月の判定（入部予定者の確定と同じ週）。成功した候補を<b>受動枠の自動の子と差し替える</b>。
        /// <see cref="ProspectService.Confirm"/> の<b>後</b>に呼ぶこと（受動枠の器ができてから差し替える）。
        /// </summary>
        /// <returns>スカウトに成功した人数。</returns>
        public static int Resolve(int joinYearIndex)
        {
            if (Current == null || Current.Resolved || Current.YearIndex != joinYearIndex) return 0;

            var rng = new Xoshiro256Random(GameSeed.Master ^ (ResolveStream ^ (ulong)joinYearIndex));
            var won = ScoutModel.Resolve(
                Current, ManagerService.Manager.Fame, HomeTier(), HasDorm, RosterService.Active, StrengthCoeff, Coeff, rng);

            var list = new List<ScoutCandidate>(won);
            ProspectService.AbsorbScouted(list);
            return list.Count;
        }

        /// <summary>寮を持っているか（距離の減点が和らぐ）。</summary>
        public static bool HasDorm => ManagerService.Facilities.LevelOf("dorm") > 0;

        /// <summary>自校の格（競合校の格と比べる基準）。</summary>
        private static Tier HomeTier() => Tiers.FromStrength(TeamOverall.Of(RosterService.Active));

        private static ScoutCandidate At(int index)
            => Current != null && index >= 0 && index < Current.Candidates.Count ? Current.Candidates[index] : null;

        /// <summary>セーブ保存。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            s.Scouting = Current;
            s.ScoutFieldSelection = SelectedField;
        }

        /// <summary>セーブ復元（旧セーブは null／おまかせ で補われる）。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s)
        {
            Current = s.Scouting;
            SelectedField = s.ScoutFieldSelection;
            _visitCounter = 0;
        }

        /// <summary>新規ゲームの完全リセット。</summary>
        public static void Reset()
        {
            Current = null;
            SelectedField = ScoutField.Omakase;
            _visitCounter = 0;
        }
    }
}

using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 入部予定者の単一ソース（設計書04 §2.1・issue #587）。従来は「年度替わりで新入生がその場で湧く」
    /// だけだったのを、<b>3月に確定（予定者として保持）→ 4月に仮入部（ロスターへ合流）</b>の2段にする。
    ///
    /// <para>生成そのものは <see cref="RosterLifecycle.ConfirmIncoming"/>（＝年度替わりが呼んでいた
    /// <see cref="ProspectGenerator.Intake"/> と同じ入力・同じ乱数消費）で、<b>確定を前に倒すだけ</b>。
    /// 人数の抽選規則も才能の配り方も変えていないので、同じ母種なら同じ新入生が来る。</para>
    ///
    /// <para>監督に見えるのは <see cref="IncomingClass.Reports"/> の見立てだけで、実能力は入部するまで
    /// 分からない。見立ての抽選は<b>生成とは別ストリーム</b>で引く（不変条件#2）＝スカウト網の段を
    /// 変えても、来る選手そのものは1ビットも動かない（動くのは「どこまで見えるか」だけ）。</para>
    /// </summary>
    public static class ProspectService
    {
        /// <summary>予定者の生成に使う決定論ストリーム（<see cref="RosterService.YearRolloverStream"/> と同一）。
        /// 年度替わりでその場生成していた頃と<b>同じ乱数源</b>を使う＝確定を前に倒しても新入生は変わらない。</summary>
        // （実体は RosterService 側の単一ソースを使う。ここでは別に持たない。）

        /// <summary>見立て（下見）の抽選に使う独立ストリーム。生成列を乱さないため専用IDを持つ。</summary>
        private const ulong SurveyStream = 0x5587_0000UL;

        private static readonly ProspectScoutingCoefficients ScoutCoeff = new ProspectScoutingCoefficients();
        private static readonly TeamStrengthCoefficients StrengthCoeff = new TeamStrengthCoefficients();

        /// <summary>
        /// いま抱えている学年1つぶん。null＝まだ一度も3月を跨いでいない。4月に合流した後も
        /// <see cref="IncomingClass.Joined"/> を立てたまま残り、「事前にどう見えていたか」を次の3月まで見せる。
        /// </summary>
        public static IncomingClass Current { get; private set; }

        /// <summary>確定済みでまだ入部していない予定者が居るか（スカウト画面の主表示の切り替え）。</summary>
        public static bool HasPending => Current != null && !Current.Joined;

        /// <summary>
        /// 3月の確定フック（<see cref="SeasonCalendar.IsProspectConfirmWeek"/> の週に呼ぶ）。
        /// すでに同じ年度ぶんを確定していれば何もしない＝冪等（週を巻き戻して再入場しても二重に引かない）。
        /// </summary>
        /// <param name="joinYearIndex">合流する年度（＝確定した年度の翌年度）。</param>
        /// <returns>この呼び出しで新たに確定したら true。</returns>
        public static bool Confirm(int joinYearIndex)
        {
            if (Current != null && Current.YearIndex == joinYearIndex) return false;

            var coefficients = new RosterCoefficients();
            var players = RosterLifecycle.ConfirmIncoming(
                RosterService.Roster, joinYearIndex, coefficients,
                RosterService.YearRng(joinYearIndex), talentCenter: RosterService.TalentCenter());

            // 事前開示精度は年契約「スカウト網」の段（design-04 §2.1 効果2）。確定した時点の段で焼き、
            // 後から契約を上げても「もう見た予定者」の見え方は変わらない（＝毎年3月に効く投資）。
            var disclosure = ManagerService.ContractEffects.ProspectDisclosure;
            var reports = ProspectScouting.Survey(
                players, disclosure, StrengthCoeff, ScoutCoeff,
                new Xoshiro256Random(GameSeed.Master ^ (SurveyStream ^ (ulong)joinYearIndex)));

            Current = new IncomingClass
            {
                YearIndex = joinYearIndex,
                Disclosure = disclosure,
                Players = new List<DevelopingPlayer>(players),
                Reports = reports,
            };
            return true;
        }

        /// <summary>
        /// 4月の合流で取り出す（<see cref="RosterService.AdvanceYear"/> から呼ぶ）。年度が違う／すでに合流済み
        /// ／そもそも確定していない場合は null を返し、呼び出し側は従来どおりその場生成へ落ちる。
        /// </summary>
        public static IReadOnlyList<DevelopingPlayer> TakeFor(int joinYearIndex)
        {
            if (Current == null || Current.Joined || Current.YearIndex != joinYearIndex) return null;
            return Current.Players;
        }

        /// <summary>合流の後始末（見立てへ安定IDを写し、選手の実体を手放す）。</summary>
        public static void MarkJoined(IReadOnlyList<DevelopingPlayer> joined)
        {
            if (Current == null || Current.Joined) return;
            Current.MarkJoined(joined);
        }

        /// <summary>セーブ保存。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s) { s.Incoming = Current; }

        /// <summary>セーブ復元（v4 以前は null＝予定者を持たない進行）。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s) { Current = s.Incoming; }

        /// <summary>新規ゲームの完全リセット。</summary>
        public static void Reset() { Current = null; }
    }
}

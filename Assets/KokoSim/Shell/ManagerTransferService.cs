// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;

namespace KokoSim.Unity.Shell
{
    /// <summary>監督オファー一覧の1行（残留 or 移籍先候補1校）。</summary>
    public sealed class TransferOfferRow
    {
        /// <summary>null＝残留。それ以外は <see cref="TransferCandidate.SchoolId"/>。</summary>
        public int? SchoolId;
        public bool IsStay;
        public string SchoolName = "";
        public string PrefectureName = "";
        public string TierLetter = "";
        public double Strength;
    }

    /// <summary>監督オファー画面に表示する一式（スナップショット）。</summary>
    public sealed class TransferOfferView
    {
        public string Headline = "";
        public string Lead = "";
        /// <summary>残留を選べるか（強制転任は不可＝拒否できない, 設計書04 §1.3）。</summary>
        public bool CanStay;
        public List<TransferOfferRow> Rows = new List<TransferOfferRow>();
    }

    /// <summary>
    /// 監督の転任判定（設計書04 §1.3）の実ゲーム導線。年度末フック（<see cref="ManagerService.ApplyYearlyGrowth"/>）
    /// から開き、プレイヤーが赴任先（or 残留）を選んで閉じる。<see cref="NewTeamService"/> と同じ
    /// 「セッション静的・UnityEngine非依存・Pending待ちをホームが拾う」作法。
    /// </summary>
    public static class ManagerTransferService
    {
        /// <summary>選択待ちか（オファー画面を出す条件）。</summary>
        public static bool Pending { get; private set; }

        private static TransferEventKind _kind = TransferEventKind.None;
        private static readonly KokoSim.Engine.Season.SeasonCalendar SeasonCalendarInstance =
            new KokoSim.Engine.Season.SeasonCalendar();
        private static List<TransferCandidate> _candidates = new List<TransferCandidate>();

        /// <summary>年度末の転任判定フックから呼ぶ。Kind=None（残留・オファーなし）なら何もしない。</summary>
        internal static void Open(TransferResult result)
        {
            if (result.Kind == TransferEventKind.None) return;
            _kind = result.Kind;
            _candidates = new List<TransferCandidate>(result.Candidates);
            Pending = true;
        }

        /// <summary>テスト・リセット用（新規ゲームの完全リセットも兼ねる）。</summary>
        public static void Clear()
        {
            Pending = false;
            _kind = TransferEventKind.None;
            _candidates.Clear();
        }

        /// <summary>セーブ保存: 選択待ちの種別と候補を DTO へ写す。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            s.ManagerTransferPending = Pending;
            s.ManagerTransferKind = (int)_kind;
            s.ManagerTransferCandidates = new List<TransferCandidate>(_candidates);
        }

        /// <summary>セーブ復元: 選択待ちの種別と候補を据える。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s)
        {
            Pending = s.ManagerTransferPending;
            _kind = (TransferEventKind)s.ManagerTransferKind;
            _candidates = new List<TransferCandidate>(s.ManagerTransferCandidates);
        }

        public static TransferOfferView BuildView()
        {
            var view = new TransferOfferView { CanStay = _kind != TransferEventKind.ForcedTransfer };
            switch (_kind)
            {
                case TransferEventKind.ForcedTransfer:
                    view.Headline = "強制転任";
                    view.Lead = "今年度をもって転任が決まりました。新しい赴任先です。";
                    break;
                case TransferEventKind.BecameFreeAgent:
                    view.Headline = "フリー監督への昇格";
                    view.Lead = "実績が認められ、フリー監督の資格を得ました。本拠地を選んでください。";
                    break;
                default:
                    view.Headline = "監督オファー";
                    view.Lead = "他校から監督就任の打診が届いています。";
                    break;
            }

            if (view.CanStay)
            {
                var self = NationService.ManagerSchool();
                view.Rows.Add(new TransferOfferRow
                {
                    SchoolId = null,
                    IsStay = true,
                    SchoolName = NationService.ManagerSchoolName,
                    PrefectureName = NationService.PrefectureName(NationService.PrefectureId),
                    Strength = self.Strength,
                    TierLetter = Tiers.FromStrength(self.Strength).ToString(),
                });
            }

            foreach (var c in _candidates)
            {
                view.Rows.Add(new TransferOfferRow
                {
                    SchoolId = c.SchoolId,
                    // 候補はオファーの時点の校名を持っている＝その後の改名（issue #946）を ID から引き直す。
                    SchoolName = NationService.DisplayName(c.SchoolId, c.SchoolName),
                    PrefectureName = NationService.PrefectureName(c.PrefectureId),
                    Strength = c.Strength,
                    TierLetter = Tiers.FromStrength(c.Strength).ToString(),
                });
            }
            return view;
        }

        /// <summary>
        /// 選択を確定する。<paramref name="schoolId"/>=null は残留（強制転任では不可＝false を返す）。
        /// 転任を選ぶと: 信頼度リセット・施設/施設基金を置いていく・部員は学校に残る（旧校は AI校として全国に残り、
        /// 転任先 AI校の部員を引き継ぐ。issue #641）・自校の同定を更新する。通算成績と配色も学校に付いて回る
        /// （旧校へ置いていき、転任先のものを引き継ぐ。issue #1261）。
        /// 部費・名声・指導力・采配・育成眼は監督個人の実績として持ち越す（触らない）。
        /// </summary>
        public static bool Confirm(int? schoolId)
        {
            if (!Pending) return false;

            if (schoolId == null)
            {
                if (_kind == TransferEventKind.ForcedTransfer) return false;
                ManagerTransferModel.ApplyDecision(ManagerService.Manager, _kind, transferred: false, ManagerService.Coefficients);
                Clear();
                return true;
            }

            TransferCandidate target = null;
            foreach (var c in _candidates) if (c.SchoolId == schoolId.Value) { target = c; break; }
            if (target == null) return false;

            // 転任は NationService.TransferManager で NationRosters を直接書き換える＝裏の全シム
            // （同じロスターを読む全国裏試合・予選成績の打ち直し等）の完了を先に待つ（issue #1226）。
            BackgroundSims.EnsureAllCompleted();

            // 歴任校の記録（issue #980）。転任先へ切り替える前に、旧校ぶんの在任区間・校名・戦績を確定して
            // 台帳へ積み、次の赴任の就任年度（退任年度+1）も同時に確定する（通算戦績バケットは転任をまたいでも
            // 単一＝校ID固定・issue #641 の方式なので、ここで基準値からの差分として切り出す）。
            // 退任年度は「いつ確定したか」で決まる: 通常は3月の総括週（週46）に ApplyYearlyGrowth(YearIndex) の
            // 中で開き、その週のうちに確定する＝YearIndex がまだ終わる年度。年度替わり側で開いた取りこぼし経路や、
            // 選ばないまま4月を越えた場合は YearIndex が繰り上がっている＝その前年度（夜間レビュー #1013）。
            var departingSchoolName = TopBarMeters.SchoolDisplayName();
            var departingStartYearIndex = GameSession.Current.PostingHistory.CurrentStartYearIndex;
            var departingEndYearIndex =
                ManagerPostingYears.EndedYearIndexAtConfirm(GameClock.YearIndex, GameClock.Week, SeasonCalendarInstance);
            GameSession.Current.PostingHistory.Close(
                departingSchoolName, departingEndYearIndex, GameSession.Current.Records.For(NationService.ManagerSchoolId));
            // 個人成績も旧校ぶんで区切る（issue #1112）。PostingHistory は集計値の差分で済むが、個人成績は
            // 選手単位なので差分できず、複写してから現本体を空にする（新しい赴任先の記録タブに
            // 旧校の選手が混ざらないようにする）。
            GameSession.Current.Stats.CloseForTransfer(departingSchoolName, departingStartYearIndex, departingEndYearIndex);
            // 去る学校の通算帳簿は学校に付いて回る（issue #1261）: いま区切った複写を、全国側の学校別通算へ据える
            // （旧校のロスターは自校の選手IDを引き継ぐのでキーはそのまま使える）。
            var closedStats = GameSession.Current.Stats.ClosedSchools;
            var departingCareer = closedStats[closedStats.Count - 1].Career;
            // 配色も学校に付いて回る: 去る直前の自校の配色を控え、転任後に旧校へ残す。
            var departingColors = TeamColorProvider.ManagerColors;

            ManagerTransferModel.ApplyDecision(ManagerService.Manager, _kind, transferred: true, ManagerService.Coefficients);
            ManagerService.ResetFacilitiesForTransfer();
            // マネージャー（NPC）も入れ替わる: 既存の学校への赴任なので2年か3年（issue #1430）。
            // 独立ストリーム（不変条件#2・年度でずらし転任を重ねても毎回違う抽選にする）。
            TeamManagerLifecycle.AssignForTransfer(
                ManagerService.Manager, new Xoshiro256Random(GameSeed.Master ^ (0x1430_0002UL ^ (ulong)GameClock.YearIndex)));
            // 部員は学校に所属する（issue #641）: 旧校は AI校として全国に残り旧部員もそこに残る。
            // 転任先 AI校の部員を引き継ぎ、転任先は全国から外れる（自校＝専用ID で大会に出る）。
            var targetSchool = NationService.SchoolById(target.SchoolId);
            // 転任先の今の名前（改名していればその名前・issue #946）。全国から外すと生成時の名前へ戻るので先に取る。
            var targetName = targetSchool != null ? targetSchool.Name : target.SchoolName;
            if (targetSchool != null)
            {
                var active = RosterService.Active;
                // 旧校は改名前の名前で全国へ戻す（自校の改名は NationService.TransferManager が改名表へ移す）。
                var departing = new DepartingSchool(
                    NationService.ManagerSchoolGeneratedName, NationService.PrefectureId, NationService.DistrictId,
                    TeamOverall.Of(active));
                var result = NationService.TransferManager(
                    departing, active, targetSchool, GameClock.YearIndex, RosterService.NextPlayerId, departingCareer);
                RosterService.AdoptTransferRoster(result.Roster, result.NextPlayerId);
                // 転任先の AI 時代の通算は、IDを振り直した新しい部員のキーへ付け替えて自校の通算にする（#1261）。
                GameSession.Current.Stats.Career.ReplaceWith(result.ArrivingCareer);
                // 戦績も学校に付いて回る（issue #1413）: 去る学校の試合は旧校へ、転任先がかつて戦った試合は自校へ。
                GameSession.Current.MatchHistory.HandOverSchool(result.OldSchoolId, target.SchoolId);
                TeamColorProvider.LeaveSchool(result.OldSchoolId, departingColors);
                TeamColorProvider.ArriveAtSchool(target.SchoolId);
            }
            else
            {
                // 候補が全国に見つからない（あり得ないはずの）場合だけ旧挙動＝1年生で作り直す。
                RosterService.ResetForTransfer(GameClock.YearIndex);
            }
            // 候補の校名は全国の表記（「〜高校」込み）。自校の校名は幹で持つ（表示側で「高校」を付ける）。
            NationService.SetSchoolIdentity(SchoolNames.Stem(targetName), target.PrefectureId, target.DistrictId);

            // 部員が丸ごと入れ替わるので前校のスタメンは意味を持たない（issue #955）。
            GameSession.Current.LastLineup = null;

            Clear();
            return true;
        }
    }
}

// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System.Collections.Generic;
using KokoSim.Engine.Career;
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
        public double Fame;
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
                    Fame = ManagerService.Manager.Fame,
                    TierLetter = Tiers.FromStrength(self.Strength).ToString(),
                });
            }

            foreach (var c in _candidates)
            {
                view.Rows.Add(new TransferOfferRow
                {
                    SchoolId = c.SchoolId,
                    SchoolName = c.SchoolName,
                    PrefectureName = NationService.PrefectureName(c.PrefectureId),
                    Strength = c.Strength,
                    Fame = c.Fame,
                    TierLetter = Tiers.FromStrength(c.Strength).ToString(),
                });
            }
            return view;
        }

        /// <summary>
        /// 選択を確定する。<paramref name="schoolId"/>=null は残留（強制転任では不可＝false を返す）。
        /// 転任を選ぶと: 信頼度リセット・施設/施設基金を置いていく・部員は新設校と同じ形で作り直す
        /// （部員は旧校に残る想定, issue「監督の転任システムが実ゲームに完全未配線」の決定）・自校の同定を更新する。
        /// 部費・名声・指導力・采配・育成眼は監督個人の実績として持ち越す（触らない）。
        /// </summary>
        public static bool Confirm(int? schoolId)
        {
            if (!Pending) return false;

            if (schoolId == null)
            {
                if (_kind == TransferEventKind.ForcedTransfer) return false;
                Clear();
                return true;
            }

            TransferCandidate target = null;
            foreach (var c in _candidates) if (c.SchoolId == schoolId.Value) { target = c; break; }
            if (target == null) return false;

            ManagerTransferModel.ApplyTransfer(ManagerService.Manager, ManagerService.Coefficients);
            ManagerService.ResetFacilitiesForTransfer();
            RosterService.ResetForTransfer();
            NationService.SetSchoolIdentity(target.SchoolName, target.PrefectureId, target.DistrictId);

            Clear();
            return true;
        }
    }
}

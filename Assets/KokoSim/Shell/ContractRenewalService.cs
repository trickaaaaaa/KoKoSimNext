using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Season;
using KokoSim.Unity.Management;

namespace KokoSim.Unity.Shell
{
    /// <summary>選べる段1つ（0＝やめる）。</summary>
    public sealed class ContractTierOption
    {
        public int Level;
        public string Label;
        /// <summary>年額[万円]。</summary>
        public double Cost;
        /// <summary>効果の1行（経営タブの契約の行と同じ文。未契約の段は空）。</summary>
        public string Effect;
        public bool Selected;
    }

    /// <summary>更新の1ページ分（系統1つ）。</summary>
    public sealed class ContractRenewalCard
    {
        public string Name;
        /// <summary>更新の前に契約していた段。</summary>
        public int CurrentLevel;
        public double CurrentCost;
        public List<ContractTierOption> Options = new List<ContractTierOption>();
    }

    /// <summary>最終ページの1行（系統1つ分の選択結果）。</summary>
    public sealed class ContractRenewalSummaryRow
    {
        public string Name;
        public string LevelText;
        public double Cost;
        /// <summary>契約中の段から変えたか。</summary>
        public bool Changed;
    }

    /// <summary>更新画面の描画材料。ページは「系統ごとのカード」＋最後の確認の1枚。</summary>
    public sealed class ContractRenewalView
    {
        public string Title;
        public int PageNumber;
        public int PageCount;
        public bool IsSummary;
        public bool CanGoBack;
        public ContractRenewalCard Card;
        public List<ContractRenewalSummaryRow> Rows = new List<ContractRenewalSummaryRow>();

        /// <summary>選んだ段の年額合計[万円]。</summary>
        public double TotalCost;
        /// <summary>部費の年間差引[万円]（経営タブの「部費 年間差引」と同じ式）。</summary>
        public double Net;
        /// <summary>いまの部費[万円]。</summary>
        public double Funds;
        /// <summary>総括のあとの部費の見込み[万円]。</summary>
        public double ProjectedFunds;
    }

    /// <summary>
    /// 年契約の更新（issue #1034）。総括（第46週）の前週に会話（events.yaml の contract_renewal）を出し、
    /// 読み終えたら契約中の年契約を1系統ずつ見せて段を選び直させ、最後に年額合計と部費の見込みを確かめて確定する。
    /// 判定は engine の <see cref="ContractRenewal"/>、選択の下書きは <see cref="ContractRenewalDraft"/> に1箇所。
    /// 確定するまで契約は変わらず、何も触らず確定すれば従来の自動更新と同じ。
    /// </summary>
    public static class ContractRenewalService
    {
        private static readonly SeasonCalendar Calendar = new SeasonCalendar();
        private static readonly CareerCoefficients Career = new CareerCoefficients();

        private static ContractRenewalDraft _draft;
        private static int _page;

        /// <summary>更新の待ち（会話を出してから確定するまで立つ）。ホームが会話の消化後に見て、画面を開く。</summary>
        public static bool Pending { get; private set; }

        /// <summary>週に入った直後に呼ぶ。更新の週で契約中の年契約があれば、会話を積んで待ちを立てる。</summary>
        public static void RaiseForWeek()
        {
            if (!ContractRenewal.ShouldRaise(Calendar, GameClock.Week, ManagerService.ContractList, ManagerService.Contracts))
                return;
            // 表に無いイベントで待ちだけ立てると回送が居座るので、先に存在を確かめる（会話が無くても更新の画面は出す）。
            if (EventService.Scheduler.Find(ContractRenewal.EventId) != null)
                EventService.PushMilestone(ContractRenewal.EventId);
            Begin();
        }

        /// <summary>撮影・デバッグ用: 判定を通さずに待ちを立てる（契約中の系統が無ければ false）。</summary>
        public static bool Begin()
        {
            var draft = new ContractRenewalDraft(ManagerService.ContractList, ManagerService.Contracts);
            if (draft.Defs.Count == 0) return false;
            _draft = draft;
            _page = 0;
            Pending = true;
            return true;
        }

        /// <summary>カード数＋確認の1枚。</summary>
        private static int PageCount => _draft == null ? 0 : _draft.Defs.Count + 1;

        private static bool OnSummary => _draft != null && _page >= _draft.Defs.Count;

        public static void Select(int level)
        {
            if (_draft == null || OnSummary) return;
            _draft.SetLevel(_draft.Defs[_page].Id, level);
        }

        /// <summary>次のページへ。確認ページでは何もしない（確定は <see cref="Confirm"/>）。</summary>
        public static void Next()
        {
            if (_draft != null && !OnSummary) _page++;
        }

        public static void Back()
        {
            if (_page > 0) _page--;
        }

        /// <summary>選んだ段を契約へ書き戻して待ちを下ろす。上げた段はすぐ効き、やめる・下げるは年度末の予約。第46週の総括はいまの段の年額を引く。</summary>
        public static void Confirm()
        {
            if (_draft != null) _draft.ApplyTo(ManagerService.Contracts);
            Clear();
        }

        /// <summary>待ちを捨てる（新規ゲーム・ロード・週を巻き戻したとき）。契約は触らない。</summary>
        public static void Clear()
        {
            _draft = null;
            _page = 0;
            Pending = false;
        }

        public static ContractRenewalView BuildView()
        {
            var v = new ContractRenewalView();
            if (_draft == null) return v;

            var set = ManagerService.Contracts;
            var catalog = ManagerService.ContractList;
            var m = ManagerService.Manager;

            v.PageCount = PageCount;
            v.PageNumber = _page + 1;
            v.IsSummary = OnSummary;
            v.CanGoBack = _page > 0;
            v.Title = v.IsSummary ? "年契約の更新" : "年契約の更新 (" + v.PageNumber + "/" + _draft.Defs.Count + ")";

            if (!v.IsSummary)
            {
                var def = _draft.Defs[_page];
                var nowLevel = System.Math.Clamp(set.LevelOf(def.Id), 0, def.MaxLevel);
                var picked = _draft.LevelOf(def.Id);
                var card = new ContractRenewalCard
                {
                    Name = def.Name,
                    CurrentLevel = nowLevel,
                    CurrentCost = def.Tiers[nowLevel].AnnualCost,
                };
                for (var lv = 0; lv <= def.MaxLevel; lv++)
                    card.Options.Add(new ContractTierOption
                    {
                        Level = lv,
                        Label = lv == 0 ? "やめる" : "Lv" + lv,
                        Cost = def.Tiers[lv].AnnualCost,
                        Effect = lv == 0 ? "" : ManagementState.ContractEffectText(def.Tiers[lv]),
                        Selected = lv == picked,
                    });
                v.Card = card;
            }

            foreach (var def in _draft.Defs)
            {
                var before = System.Math.Clamp(set.LevelOf(def.Id), 0, def.MaxLevel);
                var picked = _draft.LevelOf(def.Id);
                v.Rows.Add(new ContractRenewalSummaryRow
                {
                    Name = def.Name,
                    LevelText = picked == 0 ? "やめる" : "Lv" + picked,
                    // 総括で引かれる額＝下げた系統はいまの段のまま（解約は年度末・issue #1298）。
                    Cost = def.Tiers[System.Math.Max(picked, before)].AnnualCost,
                    Changed = picked != before,
                });
            }

            v.TotalCost = _draft.AnnualCost(set);
            v.Net = CareerEngine.AnnualFundsNet(m.Trust, m.Fame, v.TotalCost, Career);
            v.Funds = m.Funds;
            v.ProjectedFunds = CareerEngine.ProjectedFunds(m.Funds, m.Trust, m.Fame, v.TotalCost, Career);
            return v;
        }
    }
}

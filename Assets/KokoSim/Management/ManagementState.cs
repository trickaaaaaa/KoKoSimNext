using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;   // ManagerService（監督メタ・施設・カタログの単一ソース）

namespace KokoSim.Unity.Management
{
    /// <summary>
    /// 台帳の区分（施設／練習道具／備品／年契約）。区分見出しを縦に積むのをやめ、
    /// タブで排他に切り替える＝一度に出るのは1区分（最大5行）なので1画面に収まる。
    /// </summary>
    public sealed class LedgerGroup
    {
        public PurchaseKind Kind;
        public string Label = "";
        /// <summary>タブ帯の右に小さく出す補足（その区分の性質・どの財布から出るか）。</summary>
        public string Note = "";
        /// <summary>年契約の区分か（買い切りでなく毎年払い・解約できる）。</summary>
        public bool IsContract;
        public List<FacilityLedgerRow> Rows = new List<FacilityLedgerRow>();
    }

    /// <summary>台帳の1行（1系統＝1行パネル）。</summary>
    public sealed class FacilityLedgerRow
    {
        public string Id = "";
        public string Name = "";
        public int Level;
        public int MaxLevel;
        /// <summary>
        /// いまの段の効果。買い切り（施設・道具・備品）は Lv1..lv の積み上げ、
        /// 年契約はいまの段だけ（累積しない）。未所持・未契約は「—」。
        /// </summary>
        public string CurrentEffect = "";
        /// <summary>次の1段で増える効果。最大レベルなら空。</summary>
        public string NextEffect = "";
        /// <summary>次の1段の費用[万円]。年契約は次の段の年額。null＝最大レベル（これ以上上げられない）。</summary>
        public double? NextCost;
        /// <summary>資金が足りるか（最大レベルなら false）。年契約は前払いが無いので常に true。</summary>
        public bool Affordable;

        /// <summary>年契約の行か（費用の単位が「/年」になり、解約できる）。</summary>
        public bool IsContract;
        /// <summary>操作ボタンの文言（購入／契約／上げる）。</summary>
        public string ActionLabel = "購入";
        /// <summary>解約ボタンを出すか（年契約で1段以上契約中のときだけ）。</summary>
        public bool CanCancel;
    }

    /// <summary>経営画面に表示する一式（スナップショット）。</summary>
    public sealed class ManagementView
    {
        public double Funds;
        /// <summary>年度頭に入る収入[万円]（学校予算＋信頼度連動）。</summary>
        public double Income;
        public string IncomeNote = "";
        /// <summary>年度頭に出る固定支出[万円]（夏合宿・冬合宿・スカウト）。</summary>
        public double Expense;
        public string ExpenseNote = "";
        public double Net;

        /// <summary>区分の一覧（タブ帯の順）。中身は選択中の1区分ぶんだけ描く。</summary>
        public List<LedgerGroup> Groups = new List<LedgerGroup>();
        /// <summary>選択中の区分の添字（<see cref="Groups"/> の添字）。</summary>
        public int SelectedTab;
        /// <summary>選択中の区分（<see cref="Groups"/> が空なら null）。</summary>
        public LedgerGroup Selected =>
            Groups.Count == 0 ? null : Groups[System.Math.Min(System.Math.Max(SelectedTab, 0), Groups.Count - 1)];

        /// <summary>いまの練習効率係数と、全系統を最大まで上げたときの値。</summary>
        public double CoefNow;
        public double CoefMax;
        /// <summary>いまの週練習時間[分]と、全系統を最大まで上げたときの値。</summary>
        public int MinutesNow;
        public int MinutesMax;

        /// <summary>施設基金の残高[万円]（建物専用の別会計）。</summary>
        public double FacilityFund;
        /// <summary>施設基金の年間収入[万円]（学校の設備投資＋後援会・OB寄付）。</summary>
        public double FundIncome;
        public string FundNote = "";

        /// <summary>直近の操作結果（購入の成否）。空＝メッセージなし。</summary>
        public string StatusText = "";
        public bool StatusIsWarning;
    }

    /// <summary>
    /// 経営画面の状態（設計書04 §1.4 資金 / 設計書03 §4 施設・Issue #128）。
    /// 施設の購入判定・資金の減算はエンジン（<see cref="FacilityPurchase"/>）が持ち、ここは表示と入力の中継だけ
    /// （UI-BUILD-METHOD 大原則④）。年間の収入・固定支出は年度替わりに <see cref="CareerEngine.ApplyResults"/> が
    /// 自動処理するので、この画面では内訳を見せるだけで操作しない。
    /// UnityEngine 非依存（設計書06 §1）。
    /// </summary>
    public sealed class ManagementState
    {
        // 係数は全画面で同一の C#既定を共有する（data/coefficients.yaml と同値・不変条件#4）。
        private static readonly CareerCoefficients Career = new CareerCoefficients();
        private static readonly TrainingCoefficients Training = new TrainingCoefficients();

        private string _status = "";
        private bool _statusWarn;
        // 選択中の区分タブ（0=施設）。画面を開き直すと施設へ戻る＝表示だけの一時状態。
        private int _selectedTab;

        public ManagementView BuildView()
        {
            var m = ManagerService.Manager;
            var catalog = ManagerService.Catalog;
            var set = ManagerService.Facilities;

            var contracts = ManagerService.ContractEffects;
            var income = Career.AnnualBudgetBase + Career.BudgetPerTrust * m.Trust;
            // 固定支出＝合宿（年2回）＋年契約の年額合計。スカウト費は年契約「スカウト網」に含まれる
            // （engine 側 CareerEngine.ApplyResults も同じ数え方＝二重には引かない）。
            var camps = Career.SummerCampCost + Career.WinterCampCost;
            var expense = camps + contracts.AnnualCost;
            var donation = Career.FacilityFundDonationPerFame * m.Fame;
            var fundIncome = Career.FacilityFundSchoolBase + donation;

            var (coefAdd, minutesAdd) = catalog.Aggregate(set);
            var (coefMaxAdd, minutesMaxAdd) = catalog.Aggregate(MaxedSet(catalog));

            var v = new ManagementView
            {
                Funds = m.Funds,
                Income = income,
                // 内訳は金額の右へ並べるので短く保つ（単位はカード見出しの「単位: 万円」に集約）。
                IncomeNote = "学校予算 " + Career.AnnualBudgetBase.ToString("0")
                             + " ＋ 信頼度 " + (Career.BudgetPerTrust * m.Trust).ToString("0"),
                Expense = expense,
                ExpenseNote = "合宿 " + camps.ToString("0")
                              + " ／ 年契約 " + contracts.AnnualCost.ToString("0"),
                Net = income - expense,
                FacilityFund = m.FacilityFund,
                FundIncome = fundIncome,
                FundNote = "学校の設備投資 " + Career.FacilityFundSchoolBase.ToString("0")
                           + " ＋ 後援会・OB " + donation.ToString("0"),
                CoefNow = Training.FacilityCoef + coefAdd,
                CoefMax = Training.FacilityCoef + coefMaxAdd,
                MinutesNow = Training.DefaultBudgetMinutes + minutesAdd,
                MinutesMax = Training.DefaultBudgetMinutes + minutesMaxAdd,
                StatusText = _status,
                StatusIsWarning = _statusWarn,
            };

            // 区分はタブで排他に切り替える（縦に積むと19行になって1画面に入らない）。
            // 購入の操作はどの区分でも同じで、支払いに使う財布だけが区分で決まる
            // （FacilityPurchase.WalletOf が単一ソース）。
            AddGroup(v, catalog, set, m, PurchaseKind.Facility, "施設",
                "レベル制・学校の資産として残る ／ 施設基金から支払う");
            AddGroup(v, catalog, set, m, PurchaseKind.Equipment, "練習道具",
                "効果小・効果大の2段。振っているメニューの分野にだけ効く ／ 部費から支払う");
            AddGroup(v, catalog, set, m, PurchaseKind.Gear, "備品",
                "買い切り ／ 部費から支払う");
            AddContractGroup(v);

            // 区分が減ることは無いが、念のため範囲外を丸めてから返す。
            v.SelectedTab = v.Groups.Count == 0
                ? 0
                : System.Math.Min(System.Math.Max(_selectedTab, 0), v.Groups.Count - 1);
            return v;
        }

        /// <summary>台帳の区分タブを切り替える。表示だけの状態でエンジンには触れない。</summary>
        public void SelectTab(int index) => _selectedTab = index;

        /// <summary>
        /// 年契約（設計書04 §1.4a）。買い切りの3区分と違い、年額を毎年部費から払い、いつでも解約できる。
        /// 契約時の前払いは無いので購入可否の判定も無く、代わりに「部費 年間差引」が赤くなることで
        /// 抱えすぎが分かる（UI原則②: 本当の警告だけ警告色）。
        /// </summary>
        private static void AddContractGroup(ManagementView v)
        {
            var catalog = ManagerService.ContractList;
            var set = ManagerService.Contracts;
            var group = new LedgerGroup
            {
                Label = "年契約",
                Note = "毎年かかる ／ いつでも解約できる ／ 部費から支払う",
                IsContract = true,
            };
            foreach (var def in catalog.Contracts) group.Rows.Add(BuildContractRow(def, set));
            if (group.Rows.Count > 0) v.Groups.Add(group);
        }

        private static FacilityLedgerRow BuildContractRow(ContractDef def, ContractSet set)
        {
            var lv = def.MaxLevel <= 0 ? 0 : System.Math.Clamp(set.LevelOf(def.Id), 0, def.MaxLevel);

            var row = new FacilityLedgerRow
            {
                Id = def.Id,
                Name = def.Name,
                Level = lv,
                MaxLevel = def.MaxLevel,
                // 年契約は累積しない＝いまの段の効果だけを出す。
                CurrentEffect = ContractEffectText(def.Tiers[lv]),
                IsContract = true,
                CanCancel = lv > 0,
                ActionLabel = lv == 0 ? "契約" : "上げる",
            };

            if (lv < def.MaxLevel)
            {
                var next = def.Tiers[lv + 1];
                row.NextEffect = ContractEffectText(next);
                row.NextCost = next.AnnualCost;
                row.Affordable = true;   // 契約時の前払いは無い（年度頭にまとめて引かれる）
            }
            return row;
        }

        /// <summary>年契約1段の効果の1行表記。効果ゼロ（未契約・据え置きの段）は「—」。</summary>
        private static string ContractEffectText(ContractTier t)
        {
            var parts = new List<string>();
            if (t.ScoutTalentAdd > 0) parts.Add("新入生の質 +" + t.ScoutTalentAdd.ToString("0"));
            if (t.OutOfPrefectureOpponents) parts.Add("県外と練習試合");
            if (t.CoachingBattingAdd > 0) parts.Add("打撃の指導 +" + t.CoachingBattingAdd.ToString("0"));
            if (t.CoachingPitchingAdd > 0) parts.Add("投手の指導 +" + t.CoachingPitchingAdd.ToString("0"));
            if (t.CoachingDefenseAdd > 0) parts.Add("守備走塁の指導 +" + t.CoachingDefenseAdd.ToString("0"));
            return parts.Count == 0 ? "—" : string.Join(" ／ ", parts);
        }

        /// <summary>年契約の段を1つ上げる（契約する）。前払いが無いので資金判定はしない。</summary>
        public void Sign(string contractId)
        {
            var def = ManagerService.ContractList.Find(contractId);
            if (def == null)
            {
                _status = "その契約はありません（" + contractId + "）";
                _statusWarn = true;
                return;
            }

            var set = ManagerService.Contracts;
            var lv = System.Math.Clamp(set.LevelOf(contractId), 0, def.MaxLevel);
            if (lv >= def.MaxLevel)
            {
                _status = def.Name + " はすでに最上位の契約です";
                _statusWarn = true;
                return;
            }

            set.SetLevel(contractId, lv + 1);
            _status = def.Name + " を Lv" + (lv + 1) + " で契約しました（年 "
                      + def.Tiers[lv + 1].AnnualCost.ToString("0") + "万）";
            _statusWarn = false;
        }

        /// <summary>年契約を解約する（段を0へ戻す）。年額も効果もその場で消える。</summary>
        public void Cancel(string contractId)
        {
            var def = ManagerService.ContractList.Find(contractId);
            if (def == null) return;
            ManagerService.Contracts.SetLevel(contractId, 0);
            _status = def.Name + " を解約しました";
            _statusWarn = false;
        }

        private static void AddGroup(ManagementView v, FacilityCatalog catalog, FacilitySet set,
                                     Manager m, PurchaseKind kind, string label, string note)
        {
            // 買えるかどうかは「その区分の財布の残高」で決まる（部費が潤沢でも建物は建たない）。
            var balance = FacilityPurchase.BalanceOf(m, FacilityPurchase.WalletOf(kind));
            var group = new LedgerGroup { Kind = kind, Label = label, Note = note };
            foreach (var def in catalog.Facilities)
                if (def.Kind == kind)
                    group.Rows.Add(BuildRow(def, set, balance));
            if (group.Rows.Count > 0) v.Groups.Add(group);
        }

        /// <summary>施設を1段買う（エンジンが資金・上限を判定する）。結果はステータス文へ残す。</summary>
        public FacilityPurchaseResult Buy(string facilityId)
        {
            var result = FacilityPurchase.TryUpgrade(
                ManagerService.Manager, ManagerService.Facilities, ManagerService.Catalog, facilityId);

            var name = ManagerService.Catalog.Find(facilityId)?.Name ?? facilityId;
            switch (result)
            {
                case FacilityPurchaseResult.Ok:
                    _status = name + " を Lv" + ManagerService.Facilities.LevelOf(facilityId) + " へ上げました";
                    _statusWarn = false;
                    break;
                case FacilityPurchaseResult.InsufficientFunds:
                    // どちらの財布が足りないかを出す（部費が潤沢でも建物は建たないため）。
                    var def = ManagerService.Catalog.Find(facilityId);
                    var wallet = def is null || FacilityPurchase.WalletOf(def.Kind) == PurchaseWallet.Funds
                        ? "部費" : "施設基金";
                    _status = wallet + "が足りません（" + name + "）";
                    _statusWarn = true;
                    break;
                case FacilityPurchaseResult.AlreadyMaxLevel:
                    _status = name + " はすでに最大レベルです";
                    _statusWarn = true;
                    break;
                default:
                    _status = "その施設は購入できません（" + facilityId + "）";
                    _statusWarn = true;
                    break;
            }
            return result;
        }

        private static FacilityLedgerRow BuildRow(FacilityDef def, FacilitySet set, double funds)
        {
            var lv = def.MaxLevel <= 0 ? 0 : System.Math.Clamp(set.LevelOf(def.Id), 0, def.MaxLevel);

            var row = new FacilityLedgerRow
            {
                Id = def.Id,
                Name = def.Name,
                Level = lv,
                MaxLevel = def.MaxLevel,
                CurrentEffect = CumulativeEffectText(def, lv),
            };

            if (lv < def.MaxLevel)
            {
                var next = def.Tiers[lv + 1];
                row.NextEffect = EffectText(next);
                row.NextCost = next.Cost;
                row.Affordable = funds >= next.Cost;
            }
            return row;
        }

        /// <summary>保有ぶんの合計効果（Lv1..lv の積み上げ）＝ FacilityCatalog.AggregateEffects と同じ数え方。</summary>
        private static string CumulativeEffectText(FacilityDef def, int level)
        {
            double coef = 0, injProb = 1, injRec = 1, insProb = 1, insAcc = 0;
            double bat = 1, dfn = 1, run = 1, pit = 1;
            var minutes = 0;
            for (var i = 1; i <= level; i++)
            {
                var t = def.Tiers[i];
                coef += t.CoefAdd;
                minutes += t.BudgetAdd;
                injProb *= t.InjuryProbFactor;
                injRec *= t.InjuryRecoveryFactor;
                insProb *= t.InsightProbFactor;
                insAcc += t.InsightAccuracyAdd;
                bat *= t.BattingFactor;
                dfn *= t.DefenseFactor;
                run *= t.BaseRunningFactor;
                pit *= t.PitchingFactor;
            }
            return Compose(coef, minutes, injProb, injRec, insProb, insAcc, bat, dfn, run, pit);
        }

        /// <summary>1段ぶんの効果。</summary>
        private static string EffectText(FacilityUpgrade t) => Compose(
            t.CoefAdd, t.BudgetAdd, t.InjuryProbFactor, t.InjuryRecoveryFactor,
            t.InsightProbFactor, t.InsightAccuracyAdd,
            t.BattingFactor, t.DefenseFactor, t.BaseRunningFactor, t.PitchingFactor);

        /// <summary>効果の1行表記。効果ゼロ（未所持・全軸が中立）は「—」＝空セルを作らない。</summary>
        private static string Compose(double coefAdd, int minutesAdd,
                                      double injuryProb, double injuryRecovery,
                                      double insightProb, double insightAccuracy,
                                      double batting, double defense, double baseRunning, double pitching)
        {
            var parts = new List<string>();
            if (coefAdd > 0) parts.Add("練習効率 +" + coefAdd.ToString("0.00"));
            if (minutesAdd > 0) parts.Add("週練習 +" + minutesAdd + "分");
            if (injuryProb < 1.0) parts.Add("怪我 " + Percent(injuryProb));
            if (injuryRecovery < 1.0) parts.Add("全治 " + Percent(injuryRecovery));
            if (insightProb > 1.0) parts.Add("気づき " + Percent(insightProb));
            if (insightAccuracy > 0) parts.Add("精度 +" + insightAccuracy.ToString("0.00"));
            if (batting > 1.0) parts.Add("打撃の練習 " + Percent(batting));
            if (defense > 1.0) parts.Add("守備の練習 " + Percent(defense));
            if (baseRunning > 1.0) parts.Add("走塁の練習 " + Percent(baseRunning));
            if (pitching > 1.0) parts.Add("投手の練習 " + Percent(pitching));
            return parts.Count == 0 ? "—" : string.Join(" ／ ", parts);
        }

        /// <summary>倍率を増減の百分率にする（0.85→「-15%」／1.12→「+12%」）。</summary>
        private static string Percent(double factor)
        {
            var delta = (factor - 1.0) * 100.0;
            return (delta >= 0 ? "+" : "") + delta.ToString("0") + "%";
        }

        /// <summary>全系統を最大レベルにした仮の保有状態（「あとどこまで伸ばせるか」の上限表示用）。</summary>
        private static FacilitySet MaxedSet(FacilityCatalog catalog)
        {
            var set = new FacilitySet();
            foreach (var f in catalog.Facilities) set.SetLevel(f.Id, f.MaxLevel);
            return set;
        }

        /// <summary>効果の1行表記。効果ゼロ（未所持）は「—」＝空セルを作らない。</summary>
        private static string EffectText(double coefAdd, int minutesAdd)
        {
            if (coefAdd <= 0 && minutesAdd <= 0) return "—";
            if (coefAdd <= 0) return "週練習 +" + minutesAdd + "分";
            if (minutesAdd <= 0) return "練習効率 +" + coefAdd.ToString("0.00");
            return "練習効率 +" + coefAdd.ToString("0.00") + " ／ 週練習 +" + minutesAdd + "分";
        }
    }
}

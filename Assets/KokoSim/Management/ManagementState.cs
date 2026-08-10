using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;   // ManagerService（監督メタ・施設・カタログの単一ソース）

namespace KokoSim.Unity.Management
{
    /// <summary>台帳の区分見出し＋その区分の行（施設／練習道具／備品）。</summary>
    public sealed class LedgerGroup
    {
        public PurchaseKind Kind;
        public string Label = "";
        /// <summary>見出しの下に小さく出す補足（区分の性質）。</summary>
        public string Note = "";
        public List<FacilityLedgerRow> Rows = new List<FacilityLedgerRow>();
    }

    /// <summary>台帳の1行（1系統＝1行パネル）。</summary>
    public sealed class FacilityLedgerRow
    {
        public string Id = "";
        public string Name = "";
        public int Level;
        public int MaxLevel;
        /// <summary>いま保有しているレベルぶんの合計効果。未所持は「—」。</summary>
        public string CurrentEffect = "";
        /// <summary>次の1段で増える効果。最大レベルなら空。</summary>
        public string NextEffect = "";
        /// <summary>次の1段の費用[万円]。null＝最大レベル（これ以上買えない）。</summary>
        public double? NextCost;
        /// <summary>資金が足りるか（最大レベルなら false）。</summary>
        public bool Affordable;
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

        public List<LedgerGroup> Groups = new List<LedgerGroup>();

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

        public ManagementView BuildView()
        {
            var m = ManagerService.Manager;
            var catalog = ManagerService.Catalog;
            var set = ManagerService.Facilities;

            var income = Career.AnnualBudgetBase + Career.BudgetPerTrust * m.Trust;
            var expense = Career.SummerCampCost + Career.WinterCampCost + Career.ScoutCost;
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
                ExpenseNote = "合宿 " + (Career.SummerCampCost + Career.WinterCampCost).ToString("0")
                              + " ／ スカウト " + Career.ScoutCost.ToString("0"),
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

            // 区分ごとに1つの台帳へ並べる（見出しを挟むだけで、購入の操作はどれも同じ）。
            // 支払いに使う財布は区分で決まる（FacilityPurchase.WalletOf が単一ソース）。
            AddGroup(v, catalog, set, m, PurchaseKind.Facility, "施設",
                "レベル制・学校の資産として残る ／ 施設基金から支払う");
            AddGroup(v, catalog, set, m, PurchaseKind.Equipment, "練習道具",
                "効果小・効果大の2段。振っているメニューの分野にだけ効く ／ 部費から支払う");
            AddGroup(v, catalog, set, m, PurchaseKind.Gear, "備品",
                "買い切り ／ 部費から支払う");

            return v;
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

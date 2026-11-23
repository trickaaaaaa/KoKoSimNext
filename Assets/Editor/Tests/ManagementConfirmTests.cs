using System.Linq;
using KokoSim.Unity.Management;
using KokoSim.Unity.Shell;
using NUnit.Framework;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 経営タブの購入・年契約（issue #1534）。確認の問いの文言と、部費が足りない年契約を押せなくすることを固定する。
    /// </summary>
    public class ManagementConfirmTests
    {
        [SetUp]
        public void NewGame() => SaveGameIO.ResetForNewGame();

        [Test]
        public void ConfirmQuestion_PurchaseAndContract()
        {
            Assert.AreEqual("室内練習場を40万で買いますか？", ManagementState.ConfirmQuestion(
                new FacilityLedgerRow { Name = "室内練習場", NextCost = 40 }));
            Assert.AreEqual("専門コーチを年60万で契約しますか？", ManagementState.ConfirmQuestion(
                new FacilityLedgerRow { Name = "専門コーチ", NextCost = 60, IsContract = true }));
            Assert.AreEqual("", ManagementState.ConfirmQuestion(new FacilityLedgerRow { Name = "x" }));
        }

        [Test]
        public void Contract_IsNotAffordable_WhenAnnualCostExceedsFunds()
        {
            var state = new ManagementState();
            ManagerService.Manager.Funds = 0;
            var rows = ContractRows(state);
            Assume.That(rows.Length, Is.GreaterThan(0), "結べる年契約が無い（前提が崩れた）");
            foreach (var r in rows) Assert.IsFalse(r.Affordable, r.Name + " は部費0でも押せてしまう");

            ManagerService.Manager.Funds = 100000;
            foreach (var r in ContractRows(state)) Assert.IsTrue(r.Affordable, r.Name + " は部費が足りても押せない");
        }

        [Test]
        public void Sign_DoesNothing_WhenAnnualCostExceedsFunds()
        {
            var state = new ManagementState();
            ManagerService.Manager.Funds = 0;
            var row = ContractRows(state).First();
            var before = ManagerService.Contracts.LevelOf(row.Id);
            state.Sign(row.Id);
            Assert.AreEqual(before, ManagerService.Contracts.LevelOf(row.Id));
        }

        // 結べる（次の段がある・見るだけでない）年契約の行。
        private static FacilityLedgerRow[] ContractRows(ManagementState state)
            => state.BuildView().Groups.Where(g => g.IsContract)
                .SelectMany(g => g.Rows)
                .Where(r => !r.Locked && r.NextCost.HasValue && r.NextCost.Value > 0)
                .ToArray();
    }
}

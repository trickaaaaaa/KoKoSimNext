using NUnit.Framework;
using UnityEngine;
using KokoSim.Unity.Match;

namespace KokoSim.EditorTests
{
    /// <summary>対決ビューの較正の版上げ（design-18 §8.1）。</summary>
    public class DuelCalibrationUpgradeTests
    {
        [Test]
        public void V26_MovesHelmetDeeper_AndKeepsOtherTunedValues()
        {
            // v25 で保存された調整済みJSON（浅い被り＋監督が動かした別の項目）
            var saved = DuelCalibration.Default;
            saved.Version = 25;
            saved.HelmetOffset = new Vector3(-0.003f, -0.014f, -0.052f);
            saved.BatterX = -0.90f;

            var up = DuelCalibration.Upgrade(saved);

            Assert.That(up.HelmetOffset, Is.EqualTo(DuelCalibration.Default.HelmetOffset), "浅い被りが生き続けない");
            Assert.That(up.HelmetOffset.y, Is.EqualTo(-0.055f).Within(1e-6f));
            Assert.That(up.BatterX, Is.EqualTo(-0.90f), "その版で動かした項目以外は調整値のまま");
        }
    }
}

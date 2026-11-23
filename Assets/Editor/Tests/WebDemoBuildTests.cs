using System.IO;
using System.Linq;
using KokoSim.Unity.Match;
using KokoSim.UnityEditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// ブラウザ体験版のビルドで容量を削る仕掛け（WebDemoBuild・#1323）の見張り。
    /// ビルドそのもの（数分）は回さず、外すもの・戻す処理・索引の素体だけを確かめる。
    /// </summary>
    public class WebDemoBuildTests
    {
        /// <summary>残す球場（標準・甲子園）は外さず、それ以外の焼いた球場は全部外す。</summary>
        [Test]
        public void StadiumsToPark_KeepsStandardAndKoshienOnly()
        {
            var parked = WebDemoBuild.StadiumsToPark().Select(Path.GetFileNameWithoutExtension).ToList();
            foreach (var m in WebDemoBuild.KeptStadiumModels)
            {
                var name = DuelStadiumMesh.ResourceNameFor(m);
                Assert.IsNotNull(Resources.Load<DuelStadiumMesh>(name), $"残す球場 {name} が焼かれていない");
                CollectionAssert.DoesNotContain(parked, name, $"残す球場 {name} を外している");
            }
            Assume.That(parked.Count, Is.GreaterThan(0), "外す球場が手元に無い（焼いていない環境）");
            Assert.That(parked.All(n => n.StartsWith(DuelStadiumMesh.ResourceName + "_")), "球場以外を外そうとしている");
        }

        /// <summary>退避した球場が、後始末で元の場所へ戻る（途中で落ちても次のビルドで戻る仕組みの要）。</summary>
        [Test]
        public void RestoreLeftovers_MovesParkedStadiumsBack()
        {
            var target = WebDemoBuild.StadiumsToPark().FirstOrDefault();
            Assume.That(target, Is.Not.Null, "外す球場が手元に無い（焼いていない環境）");
            var guid = AssetDatabase.AssetPathToGUID(target);
            try
            {
                if (!AssetDatabase.IsValidFolder(WebDemoBuild.ParkDir))
                    AssetDatabase.CreateFolder("Assets", WebDemoBuild.ParkDir.Substring("Assets/".Length));
                Assert.That(AssetDatabase.MoveAsset(target, WebDemoBuild.ParkDir + "/" + Path.GetFileName(target)), Is.Empty);
                Assert.IsNull(Resources.Load<DuelStadiumMesh>(Path.GetFileNameWithoutExtension(target)), "退避しても Resources から引けてしまう");

                WebDemoBuild.RestoreLeftovers();

                Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(target), "元の場所へ戻っていない");
                Assert.IsFalse(AssetDatabase.IsValidFolder(WebDemoBuild.ParkDir), "退避先が残っている");
            }
            finally
            {
                WebDemoBuild.RestoreLeftovers();
            }
        }

        /// <summary>
        /// 索引が指す購入素体は、試合が差し替える先頭の枠にマテリアルを持たない。持っていると、使わない
        /// テクスチャ（2048角）がマテリアル経由でビルドに入る（ブラウザ版の容量・#1323）。
        /// 逆に2つ目以降の枠は差し替えられずに描かれるので、空にしてはいけない（空だとマゼンタになる）。
        /// </summary>
        [Test]
        public void CatalogVendorBody_DropsOnlyTheReplacedMaterialSlot()
        {
            var cat = Resources.Load<DuelAssetCatalog>(DuelAssetCatalog.ResourceName);
            Assume.That(cat, Is.Not.Null, "ビルド同梱の索引が無い環境（購入資産なし）");
            Assume.That(cat.VendorBody, Is.Not.Null, "購入素体が無い環境");
            foreach (var r in cat.VendorBody.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                Assert.IsNull(mats[0], $"{r.name} の先頭の枠にマテリアルが残っている（KokoSim/Duel/F8 ビルド同梱の索引を焼く）");
                for (int i = 1; i < mats.Length; i++)
                    Assert.IsNotNull(mats[i], $"{r.name} の {i} 番目の枠が空＝試合でマゼンタに描かれる");
            }
        }
    }
}

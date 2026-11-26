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
        /// 写真系の画像は、ビルドの間だけ WebGL 向けの最大サイズが下がり、後始末で .meta まで元どおりに戻る。
        /// </summary>
        [Test]
        public void CapTextureSizes_ShrinksForWebGLAndRestoresMeta()
        {
            var (dir, filter, maxSize) = WebDemoBuild.TextureCaps[0];
            var path = AssetDatabase.FindAssets(filter, new[] { dir }).Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
            Assume.That(path, Is.Not.Null, "縮める画像が手元に無い");
            var metaBefore = File.ReadAllText(path + ".meta");
            try
            {
                WebDemoBuild.CapTextureSizes();
                var s = ((TextureImporter)AssetImporter.GetAtPath(path)).GetPlatformTextureSettings("WebGL");
                Assert.IsTrue(s.overridden, "WebGL の上書きが立っていない");
                Assert.That(s.maxTextureSize, Is.EqualTo(maxSize));
            }
            finally
            {
                WebDemoBuild.RestoreLeftovers();
            }
            Assert.That(File.ReadAllText(path + ".meta"), Is.EqualTo(metaBefore), ".meta が元どおりに戻っていない");
        }

        /// <summary>
        /// 索引が指す購入素体は、どの枠にもマテリアルを持たない。持っていると、使わないテクスチャ（2048角）が
        /// マテリアル経由でビルドに入る（ブラウザ版の容量・#1323）。試合は全部の枠を自前の材質へ塗り替える
        /// （DuelStageRig.SpawnActor・#1610。先頭だけ替えていたころは2つ目の素肌がブラウザ版で消えた）。
        /// </summary>
        [Test]
        public void CatalogVendorBody_HasNoMaterials()
        {
            var cat = Resources.Load<DuelAssetCatalog>(DuelAssetCatalog.ResourceName);
            Assume.That(cat, Is.Not.Null, "ビルド同梱の索引が無い環境（購入資産なし）");
            Assume.That(cat.VendorBody, Is.Not.Null, "購入素体が無い環境");
            foreach (var r in cat.VendorBody.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    Assert.IsNull(mats[i], $"{r.name} の {i} 番目の枠にマテリアルが残っている（KokoSim/Duel/F8 ビルド同梱の索引を焼く）");
            }
        }
    }
}

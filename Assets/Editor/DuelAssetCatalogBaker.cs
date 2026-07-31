#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KokoSim.Unity.Match;
using UnityEditor;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 【開発用・エディタ限定】ビルド同梱の索引（<see cref="DuelAssetCatalog"/>）を焼く（F8）。
    ///
    /// 中身は**コード側の名前表から自動で埋める**（<see cref="DuelStageRig.RequiredClipNames"/>・
    /// 素体の Renderer 名）。名前を二重管理しないのが要で、この資産は索引でしかない。
    /// 既存の <c>DuelUniformBaker</c> / <c>DuelVendorRegionBaker</c> と同じ流儀。
    ///
    /// ⚠ **焼いた .asset は gitignore**（購入素体・購入クリップの GUID 参照＝実質的な再配布経路）。
    /// 手元に購入資産が無い環境では索引も焼けず、従来どおり <c>Available=false</c> に落ちる。
    /// </summary>
    public static class DuelAssetCatalogBaker
    {
        private const string Dir = "Assets/KokoSim/Resources";
        private const string AssetPath = Dir + "/" + DuelAssetCatalog.ResourceName + ".asset";

        [MenuItem("KokoSim/Duel/F8 ビルド同梱の索引を焼く")]
        public static void Bake()
        {
            Directory.CreateDirectory(Dir);
            var cat = AssetDatabase.LoadAssetAtPath<DuelAssetCatalog>(AssetPath);
            bool created = cat == null;
            if (created) cat = ScriptableObject.CreateInstance<DuelAssetCatalog>();

            var missing = new List<string>();

            cat.VendorBody = LoadOrNote<GameObject>(DuelStageRig.VendorBodyFbxPath, missing, "購入素体");
            cat.FallbackBody = LoadOrNote<GameObject>(DuelStageRig.BodyFbxPath, missing, "U2素体");
            cat.UniformMesh = LoadOrNote<Mesh>(DuelStageRig.UniformMeshAssetPath, missing, "着衣メッシュ");

            // 領域IDつきメッシュ。**素体の Renderer を並べて引く**＝名前表を別に持たない
            var regionNames = new List<string>();
            var regionMeshes = new List<Mesh>();
            if (cat.VendorBody != null)
            {
                foreach (var r in cat.VendorBody.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.name.EndsWith("Eye_Ball_Geo")) continue;   // のっぺらぼう方針で落とす側
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(
                        DuelStageRig.VendorRegionMeshDirPath + r.name + DuelStageRig.VendorRegionMeshSuffixPath);
                    if (mesh == null) { missing.Add("領域メッシュ " + r.name); continue; }
                    regionNames.Add(r.name);
                    regionMeshes.Add(mesh);
                }
            }
            cat.RegionNames = regionNames.ToArray();
            cat.RegionMeshes = regionMeshes.ToArray();

            // クリップ。**コード側の表が単一ソース**（重複は畳む）
            var names = new List<string>();
            var clips = new List<AnimationClip>();
            foreach (var name in DuelStageRig.RequiredClipNames().Distinct())
            {
                var clip = DuelStageRig.LoadClip(name);
                if (clip == null) { missing.Add("クリップ " + name); continue; }
                names.Add(name);
                clips.Add(clip);
            }
            cat.ClipNames = names.ToArray();
            cat.Clips = clips.ToArray();

            if (created) AssetDatabase.CreateAsset(cat, AssetPath);
            EditorUtility.SetDirty(cat);
            AssetDatabase.SaveAssets();

            var report = Report(cat, missing);
            Directory.CreateDirectory("out/duel-stage");
            File.WriteAllText("out/duel-stage/catalog-bake.txt", report);
            if (missing.Count > 0) Debug.LogWarning("[DuelAssetCatalogBaker]\n" + report);
            else Debug.Log("[DuelAssetCatalogBaker]\n" + report);
        }

        /// <summary>
        /// 焼いた索引がコード側の名前表を満たしているかを見る（ベイク忘れの検出）。
        ///
        /// ⚠ **エディタでは絵に出ない**。エディタのロードは AssetDatabase を先に見るので、
        /// 索引が古くても手元は正常に動き、**Player ビルドでだけ**そのクリップが消える。
        /// クリップを使う所を足したらこれを回す。
        /// </summary>
        [MenuItem("KokoSim/Duel/F8 ビルド同梱の索引を検証する")]
        public static void Verify()
        {
            var cat = AssetDatabase.LoadAssetAtPath<DuelAssetCatalog>(AssetPath);
            if (cat == null)
            {
                Debug.LogWarning("[DuelAssetCatalogBaker] 索引が無い（" + AssetPath
                    + "）。購入資産がある環境なら「F8 ビルド同梱の索引を焼く」を回す。"
                    + "無い環境ではこれが正常＝ビルドでは対決ビューが出ない（従来どおり）");
                return;
            }

            var missing = new List<string>();
            if (cat.VendorBody == null) missing.Add("購入素体");
            if (cat.FallbackBody == null) missing.Add("U2素体");
            if (cat.UniformMesh == null) missing.Add("着衣メッシュ");
            foreach (var name in DuelStageRig.RequiredClipNames().Distinct())
                if (cat.FindClip(name) == null) missing.Add("クリップ " + name);
            if (cat.VendorBody != null)
            {
                foreach (var r in cat.VendorBody.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.name.EndsWith("Eye_Ball_Geo")) continue;
                    if (cat.FindRegionMesh(r.name) == null) missing.Add("領域メッシュ " + r.name);
                }
            }

            var report = Report(cat, missing);
            Directory.CreateDirectory("out/duel-stage");
            File.WriteAllText("out/duel-stage/catalog-verify.txt", report);
            if (missing.Count > 0) Debug.LogWarning("[DuelAssetCatalogBaker] 索引が古い\n" + report);
            else Debug.Log("[DuelAssetCatalogBaker] 索引は最新\n" + report);
        }

        private static T LoadOrNote<T>(string path, List<string> missing, string label) where T : Object
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a == null) missing.Add(label + "（" + path + "）");
            return a;
        }

        private static string Report(DuelAssetCatalog cat, List<string> missing)
        {
            var sb = new StringBuilder();
            sb.AppendLine("素体: 購入=" + (cat.VendorBody != null ? cat.VendorBody.name : "—")
                + " / U2=" + (cat.FallbackBody != null ? cat.FallbackBody.name : "—")
                + " / 着衣=" + (cat.UniformMesh != null ? cat.UniformMesh.name : "—"));
            sb.AppendLine("領域メッシュ: " + cat.RegionNames.Length + "枚");
            sb.AppendLine("クリップ: " + cat.ClipNames.Length + "本 / 要求"
                + DuelStageRig.RequiredClipNames().Distinct().Count() + "本");
            if (missing.Count == 0) sb.AppendLine("欠け: なし");
            else
            {
                sb.AppendLine("欠け: " + missing.Count + "件");
                foreach (var m in missing) sb.AppendLine("  - " + m);
            }
            return sb.ToString();
        }
    }
}
#endif

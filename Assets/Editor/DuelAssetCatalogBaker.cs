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
    /// 素体の Renderer 名・<see cref="DuelGearMeshes.RequiredPaths"/>）。名前を二重管理しないのが要で、この資産は索引でしかない。
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

            var vendorFbx = LoadOrNote<GameObject>(DuelStageRig.VendorBodyFbxPath, missing, "購入素体");
            cat.VendorBody = vendorFbx != null ? SaveWithoutMaterials(vendorFbx, VendorBodyRuntimePath) : null;
            cat.FallbackBody = LoadOrNote<GameObject>(DuelStageRig.BodyFbxPath, missing, "U2素体");
            cat.UniformMesh = LoadOrNote<Mesh>(DuelStageRig.UniformMeshAssetPath, missing, "着衣メッシュ");
            cat.UmpireUniformMesh = LoadOrNote<Mesh>(DuelStageRig.UmpireMeshAssetPath, missing, "球審着衣メッシュ");

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

            // 装備FBX。**DuelGearMeshes の表が単一ソース**。購入品（OptionalPaths）は無くても欠けにしない
            var gearPaths = new List<string>();
            var gearModels = new List<GameObject>();
            foreach (var path in DuelGearMeshes.RequiredPaths.Concat(DuelGearMeshes.OptionalPaths))
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null)
                {
                    if (DuelGearMeshes.RequiredPaths.Contains(path)) missing.Add("装備 " + path);
                    continue;
                }
                gearPaths.Add(path);
                gearModels.Add(model);
            }
            cat.GearPaths = gearPaths.ToArray();
            cat.GearModels = gearModels.ToArray();

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
            if (cat.UmpireUniformMesh == null) missing.Add("球審着衣メッシュ");
            foreach (var name in DuelStageRig.RequiredClipNames().Distinct())
                if (cat.FindClip(name) == null) missing.Add("クリップ " + name);
            foreach (var path in DuelGearMeshes.RequiredPaths)
                if (cat.FindGear(path) == null) missing.Add("装備 " + path);
            foreach (var path in DuelGearMeshes.OptionalPaths)
                if (cat.FindGear(path) == null && AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                    missing.Add("装備（購入品が手元にあるのに索引に無い） " + path);
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

        /// <summary>
        /// 索引が指す購入素体（マテリアルを外した複製）。購入素体の置き場の下＝gitignore（購入素体の派生物）。
        /// </summary>
        internal const string VendorBodyRuntimePath = "Assets/DuelSpike/Vendor/Generated/VendorBody_Runtime.prefab";

        /// <summary>
        /// FBX を複製し、各 Renderer の先頭のマテリアル（試合が差し替える枠）を外したプレハブとして保存する。
        ///
        /// <b>なぜ要るか</b>: 試合では全 Renderer のマテリアルを領域塗り分けへ差し替える（DuelStageRig.SpawnActor）ので、
        /// FBX のマテリアルとそのテクスチャ（2048角×7枚・約24MB）は描画に一度も使われない。それでも FBX を
        /// 索引から指すと、マテリアル経由でテクスチャまでビルドに入っていた（ブラウザ版の容量・#1323）。
        /// FBX の取り込み設定（マテリアルを作らない）で外すと、顔のサブメッシュをマテリアルで見分ける
        /// DuelVendorRegionBaker が壊れるので、FBX は触らずに索引の側で外す。
        /// </summary>
        internal static GameObject SaveWithoutMaterials(GameObject fbx, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var go = Object.Instantiate(fbx);   // プレハブとの接続を持たない複製（FBX のマテリアルを引き継がない）
            try
            {
                go.name = fbx.name;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    // 枠は全部外す。試合は全部の枠を自前の材質へ塗り替える（DuelStageRig.SpawnActor）。
                    // 以前は先頭の枠だけ替えていて、2つ目（頭部メッシュの素肌＝手・腕）が FBX の URP Lit のまま
                    // 描かれ、ブラウザ版で手が消えた（#1323・2026-11-24）。FBX の肌テクスチャもビルドに入らなくなる
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++) mats[i] = null;
                    r.sharedMaterials = mats;
                }
                return PrefabUtility.SaveAsPrefabAsset(go, path);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
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
                + " / 着衣=" + (cat.UniformMesh != null ? cat.UniformMesh.name : "—")
                + " / 球審着衣=" + (cat.UmpireUniformMesh != null ? cat.UmpireUniformMesh.name : "—"));
            sb.AppendLine("領域メッシュ: " + cat.RegionNames.Length + "枚");
            sb.AppendLine("クリップ: " + cat.ClipNames.Length + "本 / 要求"
                + DuelStageRig.RequiredClipNames().Distinct().Count() + "本");
            sb.AppendLine("装備: " + (cat.GearPaths?.Length ?? 0) + "点 / 必須"
                + DuelGearMeshes.RequiredPaths.Length + "点＋購入品" + DuelGearMeshes.OptionalPaths.Length + "点");
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

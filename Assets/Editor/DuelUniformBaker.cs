using System.IO;
using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 着衣メッシュのベイカ（design-18 §3.5 U2方式）。素体FBXの本体メッシュから
    /// DuelUniformMeshBuilder で着衣メッシュを生成し、.asset へ焼く。
    ///
    /// 呼び出しは execute_code から KokoSim.EditorTools.DuelUniformBaker.BakeAll()
    /// （既存ハーネス DuelStageComposer.RunAll 等と同じ流儀・Edit mode 完結・シーン非汚染）。
    /// 素体は CC0（UBC）なので生成物はコミット可。
    ///
    /// 痩せ3段（Slim0=現状 / Slim1=中 / Slim2=強）を常に全部焼く＝比較シートと本番が同じ資産を見る。
    /// </summary>
    public static class DuelUniformBaker
    {
        private const string BodyFbx = "Assets/DuelSpike/UBC/SuperheroMale.fbx";
        private const string BodyMeshName = "SuperHero_Male";
        private const string OutDir = "Assets/DuelSpike/Generated";

        /// <summary>3段全部を焼いて結果サマリを返す。</summary>
        public static string BakeAll()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Bake("Body_Uniform_Slim0", KokoSim.Unity.Match.DuelUniformMeshBuilder.Slim0()));
            sb.AppendLine(Bake("Body_Uniform_Slim1", KokoSim.Unity.Match.DuelUniformMeshBuilder.Slim1()));
            sb.AppendLine(Bake("Body_Uniform_Slim2", KokoSim.Unity.Match.DuelUniformMeshBuilder.Slim2()));
            AssetDatabase.SaveAssets();
            return sb.ToString();
        }

        private static string Bake(string assetName, KokoSim.Unity.Match.DuelUniformMeshBuilder.Params p)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyFbx);
            if (prefab == null) return assetName + ": FAILED (body fbx not found)";
            SkinnedMeshRenderer body = null;
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.name == BodyMeshName) body = smr;
            if (body == null) return assetName + ": FAILED (body smr not found)";

            var mesh = KokoSim.Unity.Match.DuelUniformMeshBuilder.Build(body, p);

            Directory.CreateDirectory(OutDir);
            var path = OutDir + "/" + assetName + ".asset";
            // 参照はRig側がパス（AssetDatabase.LoadAssetAtPath）で引くため、作り直しで問題ない。
            // CopySerializedによる中身入れ替えは vertexCount=0 の壊れた資産を作った（2026-07-28実測）
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            EditorUtility.SetDirty(mesh);
            return assetName + ": verts=" + mesh.vertexCount + " tris=" + (mesh.triangles.Length / 3)
                + " bounds=" + mesh.bounds.size.ToString("F2");
        }
    }
}

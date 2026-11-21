using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 実行時のコードが名前で探すシェーダ（<c>Shader.Find("…")</c>）は、どのマテリアルからも参照されないので
    /// 製品ビルドで剥がされる。Always Included Shaders に入れておく（issue #1311。2026-11-21 の Web お試しビルドで
    /// <c>KokoSim/DuelStageProxyLambert</c> が剥がされ ArgumentNullException になった）。Editor では必ず見つかるので
    /// 絵では気づけない＝ここでソースを走査して止める。Editor 専用（Editor/ 配下・*Shooter・*Probe）は対象外。
    /// </summary>
    public class AlwaysIncludedShadersTests
    {
        private static readonly Regex FindCall = new Regex("Shader\\.Find\\(\"([^\"]+)\"\\)");

        [Test]
        public void ShadersFoundByNameAtRuntime_AreAlwaysIncluded()
        {
            var included = AlwaysIncluded();
            var missing = new List<string>();
            foreach (var (name, file) in RuntimeFindCalls())
            {
                var shader = Shader.Find(name);
                if (shader == null) { missing.Add($"{name}（{file}）: シェーダが無い"); continue; }
                if (IsBuiltinRenderPipelineShader(name)) continue;   // URP 既定のものはパイプラインが持ち込む
                if (!included.Contains(shader)) missing.Add($"{name}（{file}）");
            }
            Assert.IsEmpty(missing,
                "名前で探すシェーダが Always Included Shaders（Project Settings → Graphics）に無い＝製品ビルドで剥がされる:\n"
                + string.Join("\n", missing));
        }

        private static HashSet<Object> AlwaysIncluded()
        {
            var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0];
            var arr = new SerializedObject(gs).FindProperty("m_AlwaysIncludedShaders");
            var set = new HashSet<Object>();
            for (var i = 0; i < arr.arraySize; i++) set.Add(arr.GetArrayElementAtIndex(i).objectReferenceValue);
            return set;
        }

        private static IEnumerable<(string Name, string File)> RuntimeFindCalls()
        {
            foreach (var path in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories))
            {
                var p = path.Replace('\\', '/');
                if (p.Contains("/Editor/") || p.EndsWith("Shooter.cs") || p.EndsWith("Probe.cs")) continue;
                foreach (Match m in FindCall.Matches(File.ReadAllText(p)))
                    yield return (m.Groups[1].Value, p);
            }
        }

        private static bool IsBuiltinRenderPipelineShader(string name)
            => name.StartsWith("Universal Render Pipeline/") || name.StartsWith("Hidden/");
    }
}

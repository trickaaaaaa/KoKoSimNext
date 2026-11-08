using System;
using System.IO;
using KokoSim.Engine.Nation.Tournaments;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 大会展望の文言（data/preview-phrases.yaml・issue #784）の Unity 側プロバイダ。
    /// <see cref="SkillCatalogProvider"/> と同型＝StreamingAssets の YAML を読み、解釈はエンジンの
    /// 純パーサ <see cref="PreviewPhrasesParser"/> に委譲する（テストと同一コード＝同じ文が出る）。
    /// IO は Shell 層に隔離（不変条件#3）。読み込みに失敗したらエンジンの最小語彙で続ける。
    /// </summary>
    public static class PreviewPhrasesProvider
    {
        private const string RelativePath = "KokoSim/preview-phrases.yaml";

        private static PreviewPhrases _cached;

        public static PreviewPhrases Default
        {
            get
            {
                if (_cached != null) return _cached;
                _cached = Load();
                return _cached;
            }
        }

        /// <summary>テスト・リセット用（プロバイダのキャッシュを捨てる）。</summary>
        public static void Clear() => _cached = null;

        private static PreviewPhrases Load()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, RelativePath);
                if (File.Exists(path)) return PreviewPhrasesParser.Parse(File.ReadAllText(path));
                Debug.LogWarning($"[展望の文言] StreamingAssets に preview-phrases.yaml が無い（{path}）。最小語彙で継続。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[展望の文言] 読込失敗（{e.Message}）。最小語彙で継続。");
            }
            return PreviewPhrases.Fallback;
        }
    }
}

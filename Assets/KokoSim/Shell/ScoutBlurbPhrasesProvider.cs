using System;
using System.IO;
using KokoSim.Engine.Season;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// スカウト候補の寸評の語彙（data/scout-blurbs.yaml・issue #867）の Unity 側プロバイダ。
    /// <see cref="PreviewPhrasesProvider"/> と同型＝StreamingAssets の YAML を読み、解釈はエンジンの
    /// 純パーサ <see cref="ScoutBlurbPhrasesParser"/> に委譲する（テストと同一コード＝同じ文が出る）。
    /// IO は Shell 層に隔離（不変条件#3）。読み込みに失敗したらエンジンの最小語彙で続ける。
    /// </summary>
    public static class ScoutBlurbPhrasesProvider
    {
        private const string RelativePath = "KokoSim/scout-blurbs.yaml";

        private static ScoutBlurbPhrases _cached;

        public static ScoutBlurbPhrases Default
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

        private static ScoutBlurbPhrases Load()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, RelativePath);
                if (File.Exists(path)) return ScoutBlurbPhrasesParser.Parse(File.ReadAllText(path));
                Debug.LogWarning($"[スカウト寸評] StreamingAssets に scout-blurbs.yaml が無い（{path}）。最小語彙で継続。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[スカウト寸評] 読込失敗（{e.Message}）。最小語彙で継続。");
            }
            return ScoutBlurbPhrases.Fallback;
        }
    }
}

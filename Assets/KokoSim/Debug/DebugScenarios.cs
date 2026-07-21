#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
using System.IO;
using KokoSim.Engine.Debugging;
using UnityEngine;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// <c>data/debug/scenarios.yaml</c> の Unity 側プロバイダ（設計書17 §3.4・OPEN-QUESTIONS Q18-2）。
    ///
    /// <para>Unity は KokoSim.Config（YamlDotNet）を持たないので、<see cref="SchoolNameVocabProvider"/> と同じく
    /// 「IO は Shell/Debug 層・解釈はエンジンの純パーサ（<see cref="ScenarioYamlParser"/>）」に分ける。
    /// CLI と同一コードで読むので、同じ id が両者で別の場面になることがない。</para>
    ///
    /// <para><b>StreamingAssets には置かない</b>。Q18-2 は「data/debug/ をディレクトリ単位でリリース除外」と
    /// 決めており、StreamingAssets へ複製するとビルドに同梱されてしまう。デバッグ機能は Editor と
    /// Development Build でしか存在しないので、リポジトリの <c>data/debug/</c> を直接読むのが
    /// 「除外を1行も書かずに除外できる」最も単純な形。</para>
    ///
    /// <para>ファイルが無い場合は<b>0件で正常</b>（例外を投げない）。</para>
    /// </summary>
    public static class DebugScenarios
    {
        private static ScenarioCatalog _cached;

        /// <summary>シナリオ一覧（初回のみ読み込み。<see cref="Reload"/> で読み直す）。</summary>
        public static ScenarioCatalog Catalog => _cached ?? (_cached = Load());

        /// <summary>YAML を編集したあとに読み直す（Editorメニューから叩く）。</summary>
        public static ScenarioCatalog Reload()
        {
            _cached = Load();
            return _cached;
        }

        /// <summary>探したパス（見つからなければ null）。診断メッセージ用。</summary>
        public static string ResolvedPath { get; private set; }

        private static ScenarioCatalog Load()
        {
            try
            {
                ResolvedPath = FindPath();
                if (ResolvedPath == null) return ScenarioCatalog.Empty;
                return ScenarioYamlParser.Parse(File.ReadAllText(ResolvedPath));
            }
            catch (System.Exception e)
            {
                // 壊れたYAMLは黙って0件にしない（編集ミスに気づけなくなる）。ただし例外は投げず警告に留める
                // ＝DebugBridge の「例外を投げない」規約（設計書17 §7）を守る。
                Debug.LogWarning("[KokoSim/Debug] scenarios.yaml を読めません: " + e.Message);
                return ScenarioCatalog.Empty;
            }
        }

        /// <summary>Assets/ の親（＝リポジトリ直下）から data/debug/scenarios.yaml を探す。</summary>
        private static string FindPath()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "data/debug/scenarios.yaml");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
#endif

using System;
using System.IO;
using KokoSim.Engine.Nation;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 校名語彙（data/school-names.yaml）の Unity 側プロバイダ（school-name-vocab-plan.md C-1）。
    /// Unity は KokoSim.Config（YamlDotNet）を持たないため、StreamingAssets に置いた YAML を読み、
    /// 解釈はエンジンの純パーサ <see cref="SchoolNameVocabParser"/> に委譲する（Balance と同一コード＝決定論一致）。
    /// IO は Shell 層に隔離（不変条件#3）。決定論生成なので初回のみ読み込み静的キャッシュする。
    /// </summary>
    public static class SchoolNameVocabProvider
    {
        // StreamingAssets 配下の配置（tools でも data/ から複製する）。
        private const string RelativePath = "KokoSim/school-names.yaml";

        private static SchoolNameVocab _cached;

        /// <summary>県別地名を含む語彙。読み込み失敗時はエンジン既定（極小）へフォールバック。</summary>
        public static SchoolNameVocab Default
        {
            get
            {
                if (_cached != null) return _cached;
                _cached = Load();
                return _cached;
            }
        }

        private static SchoolNameVocab Load()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, RelativePath);
                if (File.Exists(path))
                {
                    var vocab = SchoolNameVocabParser.Parse(File.ReadAllText(path));
                    Debug.Log($"[校名語彙] 読込成功: 県別 {vocab.PlacesByPrefecture.Count} 県 / 共有地名 {vocab.PlacePrefixes.Count} 語");
                    return vocab;
                }
                Debug.LogWarning($"[校名語彙] StreamingAssets に school-names.yaml が無い（{path}）。既定語彙で継続。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[校名語彙] 読込失敗（{e.Message}）。既定語彙で継続。");
            }
            return new SchoolNameVocab();
        }
    }
}

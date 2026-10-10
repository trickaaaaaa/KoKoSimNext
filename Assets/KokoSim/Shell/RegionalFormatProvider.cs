using System;
using System.IO;
using KokoSim.Engine.Nation.Tournaments;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 秋季地区大会の枠配分（data/pref-formats/regional-tournaments.yaml）の Unity 側プロバイダ
    /// （設計書05 §1.5b）。Unity は KokoSim.Config（YamlDotNet）を持たないため、StreamingAssets に
    /// 置いた同じ YAML を読み、解釈はエンジンの純パーサ <see cref="RegionalFormatParser"/> に委譲する
    /// （<see cref="DistrictPlanProvider"/> と同型）。IO は Shell 層に隔離（不変条件#3）。
    /// 決定論なので初回のみ読み込み静的キャッシュする。
    /// </summary>
    public static class RegionalFormatProvider
    {
        private const string RelativePath = "KokoSim/pref-formats/regional-tournaments.yaml";

        private static RegionalFormatSet _cached;

        public static RegionalFormatSet Set => _cached ?? (_cached = Load());

        /// <summary>指定地区（ローマ字。<see cref="PrefectureNames.RegionOf"/> と同じキー）の枠配分。</summary>
        public static RegionalFormat ForRegion(string region) => Set.ForRegion(region);

        private static RegionalFormatSet Load()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, RelativePath);
                if (File.Exists(path))
                {
                    var set = RegionalFormatParser.Parse(File.ReadAllText(path));
                    Debug.Log($"[地区大会枠] 読込成功: {set.Regions.Count} 地区");
                    return set;
                }
                Debug.LogWarning($"[地区大会枠] StreamingAssets に regional-tournaments.yaml が無い（{path}）。空で継続。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[地区大会枠] 読込失敗（{e.Message}）。空で継続。");
            }
            return new RegionalFormatSet(Array.Empty<RegionalFormat>());
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Match.Field;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 球場カタログ（data/stadiums.yaml）の Unity 側プロバイダ（issue #905。設計書13）。
    /// <see cref="TeamColorProvider"/> と同型＝StreamingAssets の YAML（tools/sync-engine-dll.sh が複製）を読み、
    /// 解釈はエンジンの純パーサ <see cref="StadiumCatalogParser"/> に委譲する。IO は Shell 層に隔離（不変条件#3）。
    /// 読めないときは C# 既定の4球場（甲子園・市営2種・県営）で継続し、警告を1回だけ出す。
    /// </summary>
    public static class StadiumCatalogProvider
    {
        private const string RelativePath = "KokoSim/stadiums.yaml";

        private static IReadOnlyList<Stadium> _cached;

        /// <summary>C# 既定（StreamingAssets が無い環境の最後の砦）。</summary>
        private static readonly Stadium[] Fallback =
            { Stadium.Koshien, Stadium.MunicipalSmall01, Stadium.MunicipalSmall02, Stadium.PrefLarge };

        /// <summary>全球場（YAML の並び順）。</summary>
        public static IReadOnlyList<Stadium> All => _cached ?? (_cached = Load());

        /// <summary>id から球場を引く。無ければ null。</summary>
        public static Stadium Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var s in All)
                if (s.Id == id) return s;
            return null;
        }

        /// <summary>テスト・再読込用。</summary>
        public static void Clear() => _cached = null;

        private static IReadOnlyList<Stadium> Load()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, RelativePath);
                if (File.Exists(path))
                {
                    var list = StadiumCatalogParser.Parse(File.ReadAllText(path));
                    if (list != null) return list;
                    Debug.LogWarning($"[球場] {RelativePath} から1件も読めなかった。既定4球場で継続。");
                }
                else
                {
                    Debug.LogWarning($"[球場] StreamingAssets に stadiums.yaml が無い（{path}）。"
                                     + "bash tools/sync-engine-dll.sh を回す。既定4球場で継続。");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[球場] 読込失敗（{e.Message}）。既定4球場で継続。");
            }
            return Fallback;
        }
    }
}

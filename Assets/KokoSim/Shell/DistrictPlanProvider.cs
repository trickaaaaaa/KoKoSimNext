using System;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Nation.Tournaments;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 県内地区の数（data/pref-formats/*.yaml）の Unity 側プロバイダ（issue #585）。
    /// Unity は KokoSim.Config（YamlDotNet）を持たないため、StreamingAssets に置いた同じ YAML を読み、
    /// 解釈はエンジンの純パーサ <see cref="PrefFormatDistrictParser"/> に委譲する
    /// （<see cref="SchoolNameVocabProvider"/> / <see cref="TeamColorProvider"/> と同型）。
    /// IO は Shell 層に隔離（不変条件#3）。決定論なので初回のみ読み込み静的キャッシュする。
    ///
    /// 現状 <c>district_assignment: geographic</c> は神奈川(4)と兵庫(5)だけ。
    /// それ以外の県は表に載らない＝新規ゲーム設定画面で地区欄を出さない。
    /// </summary>
    public static class DistrictPlanProvider
    {
        // StreamingAssets 配下の配置（tools/sync-engine-dll.sh が data/ から複製する）。
        private const string RelativeDir = "KokoSim/pref-formats";

        private static IReadOnlyDictionary<int, int> _cached;

        /// <summary>県Id → 県内地区数。地区を持たない県は載らない。</summary>
        public static IReadOnlyDictionary<int, int> Plan => _cached ?? (_cached = Load());

        /// <summary>その県の地区数（0＝地区なし）。</summary>
        public static int DistrictsOf(int prefectureId)
            => Plan.TryGetValue(prefectureId, out var n) ? n : 0;

        /// <summary>表示名（「第N地区」で統一。YAML の districts 名は県によって運用ブロック数と食い違うため使わない）。</summary>
        public static string DistrictLabel(int districtId) => "第" + (districtId + 1) + "地区";

        private static IReadOnlyDictionary<int, int> Load()
        {
            try
            {
                var dir = Path.Combine(Application.streamingAssetsPath, RelativeDir);
                if (Directory.Exists(dir))
                {
                    var texts = new List<string>();
                    foreach (var path in Directory.GetFiles(dir, "*.yaml"))
                    {
                        // 地区表ではない（大会の地区＝地方大会の枠表）ので読まない。
                        if (Path.GetFileName(path) == "regional-tournaments.yaml") continue;
                        texts.Add(File.ReadAllText(path));
                    }
                    var plan = PrefFormatDistrictParser.BuildPlan(texts);
                    Debug.Log($"[県内地区] 読込成功: {plan.Count} 県が地区を持つ");
                    return plan;
                }
                Debug.LogWarning($"[県内地区] StreamingAssets に pref-formats が無い（{dir}）。地区なしで継続。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[県内地区] 読込失敗（{e.Message}）。地区なしで継続。");
            }
            return new Dictionary<int, int>();
        }
    }
}

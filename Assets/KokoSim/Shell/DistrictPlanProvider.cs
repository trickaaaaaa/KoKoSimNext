using System;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Nation;
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
    /// 現状 <c>district_assignment: geographic</c> は神奈川(4)・兵庫(5)・愛知(5)・北海道(10支部)。
    /// それ以外の県は表に載らない。ただし東京だけは別経路（<see cref="SummerRegions.SplitDistrictNames"/>）で
    /// 常に2地区を持つ＝夏の甲子園予選だけ都内が東東京/西東京に完全分割されるため（design-05 §1.1）。
    /// 東京を pref-formats 側に混ぜない理由は同メソッドのXMLコメントを参照（秋季大会は分割されない）。
    /// 北海道は逆に秋季側（10支部・<c>data/pref-formats/hokkaido.yaml</c>）が正で、夏の南北はそこから
    /// 導出される（<see cref="SummerRegions.RegionFor"/>）ので、ここでは他の地区あり県と同じ経路（<see cref="Plan"/>）で拾う。
    /// </summary>
    public static class DistrictPlanProvider
    {
        // StreamingAssets 配下の配置（tools/sync-engine-dll.sh が data/ から複製する）。
        private const string RelativeDir = "KokoSim/pref-formats";

        private static IReadOnlyDictionary<int, int> _cached;
        private static IReadOnlyDictionary<int, PrefFormatDistrictParser.AutumnDistrictRule> _rules;

        /// <summary>県Id → 県内地区数。地区を持たない県は載らない。</summary>
        public static IReadOnlyDictionary<int, int> Plan => _cached ?? (_cached = Load());

        /// <summary>県Id → 秋季地区予選の規則（各地区の進出校数・夏代表の推薦。issue #688）。地区を持たない県は載らない。</summary>
        public static IReadOnlyDictionary<int, PrefFormatDistrictParser.AutumnDistrictRule> Rules
            => _rules ?? (_rules = LoadRules());

        /// <summary>各地区から秋季県大会へ進む校数（地区予選を持たない県は1）。</summary>
        public static int AdvancePerDistrictOf(int prefectureId)
            => Rules.TryGetValue(prefectureId, out var r) ? r.AdvancePerDistrict : 1;

        /// <summary>夏の代表校を秋季県大会へ推薦するか（<c>seed_exemption</c>）。</summary>
        public static bool SeedExemptionOf(int prefectureId)
            => Rules.TryGetValue(prefectureId, out var r) && r.SeedExemption;

        /// <summary>その県の地区数（0＝地区なし）。東京だけ SummerRegions 側の2択を優先する。
        /// 新規開始画面の地区選択欄向け＝秋季大会の地区予選判定には <see cref="AutumnDistrictsOf"/> を使うこと
        /// （東京はここで2を返すが、秋季は分割されない＝地区予選を開いてはいけない）。</summary>
        public static int DistrictsOf(int prefectureId)
        {
            var summerSplit = SummerRegions.SplitDistrictNames(prefectureId).Count;
            if (summerSplit > 0) return summerSplit;
            return Plan.TryGetValue(prefectureId, out var n) ? n : 0;
        }

        /// <summary>
        /// その県の秋季地区予選の地区数（0＝地区予選なし・秋季県大会がいきなり始まる）。
        /// <see cref="Plan"/>（<c>data/pref-formats/*.yaml</c>）だけを見る＝<see cref="DistrictsOf"/> と違い
        /// 東京の夏2択（SummerRegions側）を混ぜない。東京は秋季側にデータを持たないので常に0。
        /// </summary>
        public static int AutumnDistrictsOf(int prefectureId)
            => Plan.TryGetValue(prefectureId, out var n) ? n : 0;

        private const int HokkaidoId = 0;

        /// <summary>
        /// 表示名。東京は夏のブラケット名（東東京/西東京）で出す（<see cref="SummerRegions.SplitDistrictNames"/>
        /// と同じ順序＝index 0/1 がそのまま Split 0/1 に対応）。北海道は10支部の実名（YAMLの districts:
        /// と1:1対応なので実名を出してよい、<see cref="SummerRegions.HokkaidoBranchNames"/>）。
        /// それ以外の県は「第N地区」で統一（YAML の districts 名は運用ブロック数と食い違うため使わない）。
        /// </summary>
        public static string DistrictLabel(int prefectureId, int districtId)
        {
            var names = SummerRegions.SplitDistrictNames(prefectureId);
            if (districtId >= 0 && districtId < names.Count) return names[districtId];
            if (prefectureId == HokkaidoId)
            {
                var branches = SummerRegions.HokkaidoBranchNames;
                if (districtId >= 0 && districtId < branches.Count) return branches[districtId];
            }
            return "第" + (districtId + 1) + "地区";
        }

        private static IReadOnlyDictionary<int, PrefFormatDistrictParser.AutumnDistrictRule> LoadRules()
        {
            var texts = ReadAllYaml();
            return texts == null
                ? new Dictionary<int, PrefFormatDistrictParser.AutumnDistrictRule>()
                : PrefFormatDistrictParser.BuildRules(texts);
        }

        /// <summary>StreamingAssets の県別 YAML を全部読む（regional-tournaments は地区表ではないので除く）。無ければ null。</summary>
        private static List<string> ReadAllYaml()
        {
            try
            {
                var dir = Path.Combine(Application.streamingAssetsPath, RelativeDir);
                if (!Directory.Exists(dir)) return null;
                var texts = new List<string>();
                foreach (var path in Directory.GetFiles(dir, "*.yaml"))
                {
                    if (Path.GetFileName(path) == "regional-tournaments.yaml") continue;
                    texts.Add(File.ReadAllText(path));
                }
                return texts;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[県内地区] 規則の読込失敗（{e.Message}）。進出校数は1校で継続。");
                return null;
            }
        }

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

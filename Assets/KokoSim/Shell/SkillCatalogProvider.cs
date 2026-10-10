using System;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Players;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// スキルカタログ（data/skills.yaml）の Unity 側プロバイダ（issue skills-yaml-not-data-driven）。
    /// <see cref="SchoolNameVocabProvider"/> と同型＝StreamingAssets の YAML を読み、解釈はエンジンの
    /// 純パーサ <see cref="SkillCatalogParser"/> に委譲する（Config層と同一コード＝表示文言の二重管理を解消）。
    /// IO は Shell 層に隔離（不変条件#3）。読み込み失敗・未知スキルは enum 名へフォールバックする。
    /// </summary>
    public static class SkillCatalogProvider
    {
        private const string RelativePath = "KokoSim/skills.yaml";

        private static Dictionary<Skill, SkillCatalogEntry> _cached;

        private static Dictionary<Skill, SkillCatalogEntry> Catalog
        {
            get
            {
                if (_cached != null) return _cached;
                _cached = Load();
                return _cached;
            }
        }

        /// <summary>選手詳細のスキル欄に出す表示名。カタログに無ければ enum 名へフォールバック。</summary>
        public static string Name(Skill s)
            => Catalog.TryGetValue(s, out var e) && !string.IsNullOrEmpty(e.Name) ? e.Name : s.ToString();

        /// <summary>選手詳細のスキル欄に出す説明文。カタログに無ければ空文字。</summary>
        public static string Description(Skill s)
            => Catalog.TryGetValue(s, out var e) ? e.Description : "";

        /// <summary>テスト・リセット用（プロバイダのキャッシュを捨てる）。</summary>
        public static void Clear() => _cached = null;

        private static Dictionary<Skill, SkillCatalogEntry> Load()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, RelativePath);
                if (File.Exists(path))
                {
                    var entries = SkillCatalogParser.Parse(File.ReadAllText(path));
                    var map = new Dictionary<Skill, SkillCatalogEntry>();
                    foreach (var e in entries) map[e.Id] = e;
                    Debug.Log($"[スキルカタログ] 読込成功: {map.Count} 件");
                    return map;
                }
                Debug.LogWarning($"[スキルカタログ] StreamingAssets に skills.yaml が無い（{path}）。enum名で継続。");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[スキルカタログ] 読込失敗（{e.Message}）。enum名で継続。");
            }
            return new Dictionary<Skill, SkillCatalogEntry>();
        }
    }
}

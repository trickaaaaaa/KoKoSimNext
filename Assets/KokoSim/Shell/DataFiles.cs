using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// data/ の YAML をゲームから読む唯一の入口（Web 体験版 #1323）。
    ///
    /// <para>正典は data/ のまま。<c>tools/sync-engine-dll.sh</c> が <c>Assets/Resources/KokoSimData/</c> へ複製し
    /// （Git 管理外の生成物・issue #785）、ここが <see cref="Resources.Load{T}(string)"/> の TextAsset として読む。
    /// 以前は StreamingAssets をファイルとして読んでいたが、ブラウザ版では StreamingAssets が URL になり同期で読めない
    /// （2026-11-21 の Web お試しビルドで全部「無い」になった）。Resources ならブラウザでも Windows でも同じ書き方で、
    /// その場で読める＝起動の手順を変えずに済む。</para>
    ///
    /// <para>呼び出し側の相対パスは従来の <c>"KokoSim/xxx.yaml"</c> のまま渡せる（先頭の KokoSim/ と拡張子を外して引く）。</para>
    /// </summary>
    public static class DataFiles
    {
        /// <summary>Resources の中の置き場所（Assets/Resources/KokoSimData/）。</summary>
        public const string ResourcesRoot = "KokoSimData";

        private const string LegacyPrefix = "KokoSim/";

        /// <summary>1ファイル読む。無ければ false。</summary>
        public static bool TryReadText(string relativePath, out string text)
        {
            var asset = Resources.Load<TextAsset>(ResourceKey(relativePath));
            text = asset != null ? asset.text : null;
            return asset != null;
        }

        /// <summary>フォルダ直下の YAML を全部読む（名前順＝どの環境でも同じ順）。無ければ空。</summary>
        public static IReadOnlyList<(string Name, string Text)> ReadAll(string relativeDir)
        {
            var assets = Resources.LoadAll<TextAsset>(ResourceKey(relativeDir));
            return assets
                .OrderBy(a => a.name, StringComparer.Ordinal)
                .Select(a => (a.name, a.text))
                .ToList();
        }

        /// <summary>ログに出す場所の表記。</summary>
        public static string Describe(string relativePath) => "Resources/" + ResourceKey(relativePath);

        private static string ResourceKey(string relativePath)
        {
            var p = relativePath.Replace('\\', '/');
            if (p.StartsWith(LegacyPrefix, StringComparison.Ordinal)) p = p.Substring(LegacyPrefix.Length);
            if (p.EndsWith(".yaml", StringComparison.Ordinal)) p = p.Substring(0, p.Length - ".yaml".Length);
            return ResourcesRoot + "/" + p;
        }
    }
}

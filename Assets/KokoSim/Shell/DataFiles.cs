using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// <para><b>起動時にメインスレッドで全部読んで、中身の文字列を控える</b>（<see cref="Preload"/>）。
    /// <see cref="Resources.Load{T}(string)"/> と <c>TextAsset.text</c> はメインスレッドからしか呼べない。各 Provider は
    /// 初めて使われたときに読む作りなので、大会の後始末（結果画面の OK → <c>BackgroundGameOp</c> で裏のスレッド）で
    /// 初めて読まれると例外になり、空のまま進んでいた（2026-11-22: 地区大会の枠が読めず、地区大会→神宮→センバツの
    /// 連鎖が毎回切れた）。控えた後はどのスレッドからでも同じ中身を返す。</para>
    ///
    /// <para>どのファイルがあるかは sync-engine-dll.sh が書く一覧（<c>KokoSimData/_index.txt</c>）で知る
    /// （Resources にはフォルダの中身をパスつきで列挙する手段が無い）。</para>
    ///
    /// <para>呼び出し側の相対パスは従来の <c>"KokoSim/xxx.yaml"</c> のまま渡せる（先頭の KokoSim/ と拡張子を外して引く）。</para>
    /// </summary>
    public static class DataFiles
    {
        /// <summary>Resources の中の置き場所（Assets/Resources/KokoSimData/）。</summary>
        public const string ResourcesRoot = "KokoSimData";

        /// <summary>sync-engine-dll.sh が書く一覧（KokoSimData からの相対パス・拡張子なし・1行1件）。</summary>
        public const string IndexKey = ResourcesRoot + "/_index";

        private const string LegacyPrefix = "KokoSim/";

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _texts;   // キー＝KokoSimData からの相対パス（拡張子なし）
        private static int _mainThreadId = -1;

        /// <summary>
        /// 全部の YAML を読んで控える。起動時（最初のシーンを読む前）に1回走る。Play に入るたびに読み直す
        /// （ドメインリロードを切った Editor でも、編集した YAML が次の Play に出る）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Preload()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = Resources.Load<TextAsset>(IndexKey);
            if (index == null)
            {
                Debug.LogError("[DataFiles] Resources/" + IndexKey + ".txt が無い。tools/sync-engine-dll.sh を回すこと"
                               + "（無いままだと、メインスレッドの外で初めて読まれたデータが空になる）。");
            }
            else
            {
                foreach (var raw in index.text.Split('\n'))
                {
                    var key = raw.Trim();
                    if (key.Length == 0) continue;
                    var asset = Resources.Load<TextAsset>(ResourcesRoot + "/" + key);
                    if (asset == null) { Debug.LogError("[DataFiles] 一覧にあるのに読めない: Resources/" + ResourcesRoot + "/" + key); continue; }
                    texts[key] = asset.text;
                }
            }
            lock (Gate) _texts = texts;
        }

        /// <summary>1ファイル読む。無ければ false。</summary>
        public static bool TryReadText(string relativePath, out string text)
        {
            var key = Key(relativePath);
            var texts = Texts();
            if (texts != null && texts.TryGetValue(key, out text)) return true;

            // 一覧に無い（一覧が古い・Play 前の Editor 等）。メインスレッドならその場で読める
            if (OnMainThread())
            {
                var asset = Resources.Load<TextAsset>(ResourcesRoot + "/" + key);
                text = asset != null ? asset.text : null;
                if (asset != null) lock (Gate) { if (_texts != null) _texts[key] = text; }
                return asset != null;
            }
            Debug.LogError("[DataFiles] メインスレッドの外で、控えに無いデータを読もうとした: " + Describe(relativePath)
                           + "（一覧 _index.txt に無い＝sync-engine-dll.sh を回すこと）");
            text = null;
            return false;
        }

        /// <summary>フォルダの下の YAML を全部読む（サブフォルダも含む・名前順＝どの環境でも同じ順）。無ければ空。</summary>
        public static IReadOnlyList<(string Name, string Text)> ReadAll(string relativeDir)
        {
            var dir = Key(relativeDir).TrimEnd('/');
            var texts = Texts();
            if (texts != null)
            {
                var prefix = dir + "/";
                var hits = texts.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(kv => (Name: kv.Key.Substring(kv.Key.LastIndexOf('/') + 1), Text: kv.Value))
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();
                if (hits.Count > 0 || !OnMainThread()) return hits;
            }
            if (!OnMainThread())
            {
                Debug.LogError("[DataFiles] メインスレッドの外で、控えが無いままフォルダを読もうとした: " + Describe(relativeDir));
                return new List<(string, string)>();
            }
            var assets = Resources.LoadAll<TextAsset>(ResourcesRoot + "/" + dir);
            return assets
                .OrderBy(a => a.name, StringComparer.Ordinal)
                .Select(a => (a.name, a.text))
                .ToList();
        }

        /// <summary>ログに出す場所の表記。</summary>
        public static string Describe(string relativePath) => "Resources/" + ResourcesRoot + "/" + Key(relativePath);

        private static Dictionary<string, string> Texts()
        {
            lock (Gate)
            {
                if (_texts != null) return _texts;
            }
            // Play 前の Editor（EditMode のテスト・メニュー）では Preload が走っていない。メインスレッドならここで読む
            if (OnMainThread() || _mainThreadId < 0 && IsUnityMainThreadGuess())
            {
                Preload();
                lock (Gate) return _texts;
            }
            return null;
        }

        private static bool OnMainThread()
            => _mainThreadId >= 0 ? Thread.CurrentThread.ManagedThreadId == _mainThreadId : IsUnityMainThreadGuess();

        /// <summary>Preload 前に主スレッドかを推す。スレッドプールのスレッドでなければ主スレッドとみなす（Editor の起動直後用）。</summary>
        private static bool IsUnityMainThreadGuess() => !Thread.CurrentThread.IsThreadPoolThread;

        private static string Key(string relativePath)
        {
            var p = relativePath.Replace('\\', '/');
            if (p.StartsWith(LegacyPrefix, StringComparison.Ordinal)) p = p.Substring(LegacyPrefix.Length);
            if (p.EndsWith(".yaml", StringComparison.Ordinal)) p = p.Substring(0, p.Length - ".yaml".Length);
            return p;
        }
    }
}

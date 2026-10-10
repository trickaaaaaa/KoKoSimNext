using System;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Season.Events;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// イベント表（data/events.yaml）の Unity 側プロバイダ（issue #609）。
    /// <see cref="TeamColorProvider"/> と同型＝StreamingAssets の YAML を読み、解釈はエンジンの
    /// 純パーサ <see cref="EventTableParser"/> に委譲する。IO は Shell 層に隔離（不変条件#3）。
    ///
    /// <para>読めなければ<b>空表</b>で継続する（イベントが出ないだけで進行は止まらない）。
    /// 黙って空になると「何も起きない週送り」に戻って気づけないので、必ず警告を出す。</para>
    /// </summary>
    public static class EventTableProvider
    {
        private const string RelativePath = "KokoSim/events.yaml";

        private static IReadOnlyList<GameEvent> _cached;

        /// <summary>イベント表。読み込み失敗時は空表。</summary>
        public static IReadOnlyList<GameEvent> Events => _cached ?? (_cached = Load());

        /// <summary>テスト・リセット用（キャッシュを捨てて次回に読み直す）。</summary>
        public static void Clear() => _cached = null;

        private static IReadOnlyList<GameEvent> Load()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, RelativePath);
                if (File.Exists(path))
                {
                    var events = EventTableParser.Parse(File.ReadAllText(path));
                    if (events.Count > 0)
                    {
                        Debug.Log($"[イベント] 読込成功: {events.Count} 件");
                        return events;
                    }
                    Debug.LogWarning($"[イベント] {RelativePath} から1件も読めなかった。イベント無しで継続。");
                }
                else
                {
                    Debug.LogWarning($"[イベント] StreamingAssets に events.yaml が無い（{path}）。イベント無しで継続。");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[イベント] 読込失敗（{e.Message}）。イベント無しで継続。");
            }
            return Array.Empty<GameEvent>();
        }
    }
}

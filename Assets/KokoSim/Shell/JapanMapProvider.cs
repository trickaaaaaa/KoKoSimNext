using KokoSim.Engine.Nation;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 日本地図データ（data/japan-map.yaml・issue #1410）の Unity 側プロバイダ。
    /// <see cref="StadiumCatalogProvider"/> と同型＝Resources の YAML（<see cref="DataFiles"/>）を読み、
    /// 解釈は engine の <see cref="JapanMapParser"/> に委譲する。読めなければ null（画面側が県の一覧へ落ちる）。
    /// </summary>
    public static class JapanMapProvider
    {
        private const string RelativePath = "KokoSim/japan-map.yaml";

        private static JapanMap _cached;
        private static bool _loaded;

        public static JapanMap Map
        {
            get
            {
                if (_loaded) return _cached;
                if (DataFiles.TryReadText(RelativePath, out var text))
                {
                    _cached = JapanMapParser.Parse(text);
                    if (_cached == null) Debug.LogWarning("[地図] " + RelativePath + " を解釈できなかった。");
                }
                else
                {
                    Debug.LogWarning("[地図] " + DataFiles.Describe(RelativePath) + " が読めない。tools/sync-engine-dll.sh を回すこと。");
                }
                _loaded = true;
                return _cached;
            }
        }

        public static void Clear() { _cached = null; _loaded = false; }
    }
}

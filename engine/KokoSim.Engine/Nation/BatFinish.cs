namespace KokoSim.Engine.Nation;

/// <summary>金属バットのバレルの仕上げ（対決ビューの表示専用・監督決定 2026-11-15: 銀・黒・金。木製は混ぜない）。</summary>
public enum BatFinish { Silver, Black, Gold }

/// <summary>
/// 打者からバットの仕上げを決める純関数（乱数不使用・<c>string.GetHashCode</c> 禁止＝チームカラーの校ID選択と同じ作法）。
/// 試合をまたいでも同じ選手は同じ色になる。
/// </summary>
public static class BatFinishes
{
    public const int Count = 3;

    /// <summary>
    /// 選手IDがあればIDで、無ければ（相手校の生成選手）名前で決める。名前は FNV-1a で数値化する。
    /// </summary>
    public static BatFinish For(int? playerId, string? name)
    {
        unchecked
        {
            uint key;
            if (playerId is { } id) key = (uint)id;
            else
            {
                key = 2166136261u;
                if (name != null)
                    foreach (var ch in name) key = (key ^ ch) * 16777619u;
                key ^= 0xA5A5_0000u;
            }
            uint x = key + 0x9E3779B9u;
            x = (x ^ (x >> 16)) * 0x85EBCA6Bu;
            x = (x ^ (x >> 13)) * 0xC2B2AE35u;
            x ^= x >> 16;
            return (BatFinish)(int)(x % Count);
        }
    }
}

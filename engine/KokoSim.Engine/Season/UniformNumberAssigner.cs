using System;
using System.Collections.Generic;
using System.Linq;

namespace KokoSim.Engine.Season;

/// <summary>
/// 背番号（1〜20＝ベンチ入り、0＝ベンチ外, 設計書06 §3.3b）の割当・検証を集約する純データ操作。
/// 監督がメンバー設定画面で自由割当する前提だが、ロスター内で 1〜20 は一意（重複させない）を保証する。
/// 乱数を一切含まない決定論的操作（不変条件#2）。UnityEngine 非依存（不変条件#3）。
/// </summary>
public static class UniformNumberAssigner
{
    /// <summary>ベンチ入り上限＝背番号の最大値（1〜20）。</summary>
    public const int BenchSize = 20;

    /// <summary>
    /// 総合力（<see cref="DevelopingPlayer.AverageLevel"/>）上位 <see cref="BenchSize"/> 名に
    /// 背番号 1〜20 を降順で、残りは 0（ベンチ外）を割り当てる。既存割当は破棄して全再割当する。
    /// 同点は元のロスター順を保つ（LINQ の安定ソート＝決定論）。メンバー設定画面の初期状態に使う。
    /// </summary>
    public static void AutoAssign(IReadOnlyList<DevelopingPlayer> roster)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));

        var ranked = roster
            .Select((player, index) => (player, index))
            .OrderByDescending(t => t.player.AverageLevel())
            .ToList();

        for (var rank = 0; rank < ranked.Count; rank++)
            ranked[rank].player.UniformNumber = rank < BenchSize ? rank + 1 : 0;
    }

    /// <summary>
    /// 指定選手に背番号 <paramref name="number"/>（1〜20）を割り当てる。自由割当のため、同じ背番号を
    /// 既に持つ他選手がいれば 0（ベンチ外）へ退避し、一意性を保つ。<paramref name="player"/> が別の背番号を
    /// 持っていたらそれは解放される。
    /// </summary>
    public static void Assign(IReadOnlyList<DevelopingPlayer> roster, DevelopingPlayer player, int number)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));
        if (player is null) throw new ArgumentNullException(nameof(player));
        if (number < 1 || number > BenchSize)
            throw new ArgumentOutOfRangeException(nameof(number), number, $"背番号は 1〜{BenchSize} の範囲。");

        // 同番号を持つ他選手をベンチ外へ退避（重複排除）。
        foreach (var other in roster)
            if (!ReferenceEquals(other, player) && other.UniformNumber == number)
                other.UniformNumber = 0;

        player.UniformNumber = number;
    }

    /// <summary>
    /// 選手を背番号 <paramref name="number"/>（1〜20）へ配置する。その背番号を既に他選手が持っていれば、
    /// その選手は <paramref name="player"/> の元背番号（0=ベンチ外）を引き継ぐ＝交換。空き番号なら単純移動
    /// （元背番号は空く）。UIの「選択→配置／交換」操作の主処理。
    /// </summary>
    public static void Place(IReadOnlyList<DevelopingPlayer> roster, DevelopingPlayer player, int number)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));
        if (player is null) throw new ArgumentNullException(nameof(player));
        if (number < 1 || number > BenchSize)
            throw new ArgumentOutOfRangeException(nameof(number), number, $"背番号は 1〜{BenchSize} の範囲。");

        var old = player.UniformNumber;
        if (old == number) return;

        DevelopingPlayer? holder = null;
        foreach (var other in roster)
            if (!ReferenceEquals(other, player) && other.UniformNumber == number) { holder = other; break; }

        player.UniformNumber = number;
        if (holder != null) holder.UniformNumber = old;   // 交換（old が 0 なら実質ベンチ送り）
    }

    /// <summary>2選手の背番号を入れ替える（片方または両方が 0=ベンチ外でも可）。プール上での交換に使う。</summary>
    public static void SwapPlayers(DevelopingPlayer a, DevelopingPlayer b)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (b is null) throw new ArgumentNullException(nameof(b));
        (a.UniformNumber, b.UniformNumber) = (b.UniformNumber, a.UniformNumber);
    }

    /// <summary>指定選手をベンチ外（背番号 0）にする。</summary>
    public static void Clear(DevelopingPlayer player)
    {
        if (player is null) throw new ArgumentNullException(nameof(player));
        player.UniformNumber = 0;
    }

    /// <summary>
    /// ロスターの背番号割当が整合しているか（各背番号が 0〜20 の範囲で、1〜20 に重複が無いか）を検証する。
    /// </summary>
    public static bool Validate(IReadOnlyList<DevelopingPlayer> roster)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));

        var seen = new HashSet<int>();
        foreach (var player in roster)
        {
            var n = player.UniformNumber;
            if (n < 0 || n > BenchSize) return false;
            if (n == 0) continue;              // ベンチ外は重複可
            if (!seen.Add(n)) return false;    // 1〜20 の重複は不整合
        }
        return true;
    }
}

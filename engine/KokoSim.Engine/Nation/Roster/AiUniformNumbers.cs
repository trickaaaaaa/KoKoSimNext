using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校（相手校）の背番号採番（design-06 §3.3b）。自校はプレイヤーが自由に割り当てる
/// （<see cref="Season.UniformNumberAssigner"/>）が、AI校はベンチ入り20人へ機械的に振る。
/// 規則:
/// <list type="bullet">
/// <item>エース（マウンド組の首位）＝<see cref="AceNumber"/>。温存で先発を外れても不変
///   ＝球数台帳キー (校ID, 背番号) の同定が安定する（#41）</item>
/// <item>先発野手8人＝守備位置番号2〜9。<b>マウンド組の選手が守備で先発したらそちらが優先</b>
///   ＝二番手投手が野手として出る日は背番号も2〜9になる（二刀流運用）</item>
/// <item>二番手投手＝<see cref="SecondPitcherNumber"/>（高校野球の慣例。最初の継投で出てくる番号）</item>
/// <item>3枚目以降の控え投手＝11〜20 の空き番号から校ごとの決定論ストリームで2つ引く
///   ＝どの校も同じ番号（旧実装の18・19）になる単調さを崩す</item>
/// <item>控え野手＝残る番号を若い順（強い順）</item>
/// </list>
///
/// 控え投手の番号は<b>詰めて配る</b>＝二番手が守備で先発した日はその下の投手が10を着ける。
/// 20人登録で10番が欠番になるのは不自然なので、番号はマウンド順位に紐付けず「控えに座った順」で配る
/// （＝同じ投手の番号が日によって動きうる。旧実装（18→19→20）と同じ性質）。
/// 若番ほど上位なので <see cref="Match.Game.TeamReserves"/>（背番号順）における投手どうしの並びは
/// 順位順のまま＝継投の既定指名も当日の出来のサンプリング順も従来と一致する（乱数消費不変・帯不変）。
/// 乱数はシード付きの決定論ストリームだけ（不変条件#2）。
/// </summary>
public static class AiUniformNumbers
{
    /// <summary>ベンチ入り上限＝背番号の最大値。</summary>
    public const int BenchSize = 20;

    /// <summary>エースの背番号（温存日も不変）。</summary>
    public const int AceNumber = 1;

    /// <summary>二番手投手（＝最初の継投で上がる控え投手）の背番号。</summary>
    public const int SecondPitcherNumber = 10;

    /// <summary>控え投手の枠数（二番手＋3枚目・4枚目）。</summary>
    public const int ReservePitcherSlots = 3;

    /// <summary>控え野手の枠数。</summary>
    public const int BenchFielderSlots = 8;

    /// <summary>散らす番号の下限（二番手の次）。</summary>
    private const int ScatterFloor = SecondPitcherNumber + 1;

    /// <summary>採番表の専用ストリーム用ソルト（他の Fork ソルトと衝突しない値）。</summary>
    private const ulong Salt = 0x1BAC_0000UL;

    /// <summary>
    /// (校ID, 年度) から採番表を引く（永続ロスター経路の単一入口）。同じ校・同じ年度なら常に同じ表
    /// ＝大会を跨いでも背番号が動かない（球数台帳キー #41 と同じ安定性）。
    /// </summary>
    public static AiNumberLayout For(int schoolId, int yearIndex)
        => Draw(new Xoshiro256Random(Salt ^ (ulong)(long)schoolId ^ ((ulong)(long)yearIndex * 0x9E37_79B9UL)));

    /// <summary>
    /// 採番表を引く。<paramref name="seed"/> は校ごと・年度ごとに固定の決定論ストリーム
    /// （<see cref="For"/> か、使い捨て生成では主RNGの Fork）。
    /// </summary>
    public static AiNumberLayout Draw(IRandomSource seed)
    {
        var pool = new List<int>(BenchSize - ScatterFloor + 1);
        for (var n = ScatterFloor; n <= BenchSize; n++) pool.Add(n);   // 11〜20 の10個

        // 3枚目以降の控え投手ぶんを引く。実際に使うのは控え投手の人数ぶんだけで、余った番号は
        // 控え野手へ回る（<see cref="AiNumberLayout.BenchFielders"/> は控え投手枠を3人とも使う前提の既定値）。
        var scattered = new List<int>(ReservePitcherSlots - 1);
        for (var i = 0; i < ReservePitcherSlots - 1; i++)
        {
            var index = seed.NextInt(0, pool.Count);
            scattered.Add(pool[index]);
            pool.RemoveAt(index);
        }
        scattered.Sort();   // 若番ほど上位＝マウンド順位と番号の大小を一致させる（控えの並び順を従来と同じにする）

        var mound = new List<int>(1 + ReservePitcherSlots) { AceNumber, SecondPitcherNumber };
        mound.AddRange(scattered);
        return new AiNumberLayout(mound, pool);   // pool は残り8番号（昇順のまま）
    }
}

/// <summary>
/// AI校の採番表（<see cref="AiUniformNumbers.Draw"/> の戻り値）。
/// </summary>
/// <param name="Mound">マウンド順位（0=エース）→背番号。長さは 1＋<see cref="AiUniformNumbers.ReservePitcherSlots"/>。</param>
/// <param name="BenchFielders">控え投手が3人とも居るときに控え野手へ配る番号（若い順）。
/// 控え投手が欠けた校では余った番号も控え野手へ回るので、呼び出し側が「使われなかった番号」を
/// 若い順に詰め直す（<see cref="AiTeamBuilder"/>）。長さは <see cref="AiUniformNumbers.BenchFielderSlots"/>。</param>
public sealed record AiNumberLayout(IReadOnlyList<int> Mound, IReadOnlyList<int> BenchFielders);

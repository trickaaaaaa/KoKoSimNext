using System;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 育成選手（自校）の表示Lv⇔物理Lv（issue #1080・<see cref="AbilityScale"/>）。生成は今の物差しで作り、出口で
/// <see cref="ToDisplay"/> を1回だけ当てる。対象は能力の現在値・才能上限・精神力・捕手リード（とそれぞれの上限）・投手センス（と見立て）。
/// 弾道（型の軸）・守備位置適性（地力に掛ける補正）・経験値・性格は対象外。
/// </summary>
public static class DevelopingScale
{
    /// <summary>今の物差し（物理Lv）→表示Lv（一律 −Δ・下限1）。</summary>
    public static void ToDisplay(DevelopingPlayer p) => Shift(p, -AbilityScale.Delta);

    /// <summary>表示Lv→今の物差し（物理Lv）（一律 +Δ）。</summary>
    public static void ToPhysical(DevelopingPlayer p) => Shift(p, AbilityScale.Delta);

    /// <summary>
    /// 一律に <paramref name="by"/> だけずらす（下げるときは表示の下限1で止める）。セーブの移行（保存時の Δ と今の Δ の差）にも使う。
    /// </summary>
    public static void Shift(DevelopingPlayer p, int by)
    {
        if (by == 0) return;
        foreach (var k in AbilityKinds.All)
        {
            if (k == AbilityKind.LaunchTendency) continue;
            p.SetLevel(k, Move(p.Level(k), by));
            p.SetCap(k, Move(p.Cap(k), by));
        }
        p.Mental = Move(p.Mental, by);
        p.MentalCap = Move(p.MentalCap, by);
        p.Lead = Move(p.Lead, by);
        p.LeadCap = Move(p.LeadCap, by);
        p.PitcherSense = Move(p.PitcherSense, by);
        if (p.PitcherSenseReading is { } r) p.PitcherSenseReading = Move(r, by);
    }

    private static int Move(int v, int by) => by < 0 ? Math.Max(AbilityScale.DisplayMin, v + by) : v + by;
}

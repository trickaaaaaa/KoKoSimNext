using System;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>成長イベントで上がった能力の「見せる量」（issue #864）。球速だけは内部値でなく km/h の伸びで出す。</summary>
public static class GrowthGainDisplay
{
    /// <summary>
    /// 球速の伸び[km/h]（画面に出る整数 km/h の差＝上がる前の最速と上がった後の最速をそれぞれ四捨五入して引く）。
    /// <paramref name="levelAfter"/> は上がった後の球速Level、<paramref name="amount"/> は実際に上がった内部値の幅。
    /// </summary>
    public static int VelocityGainKmh(int levelAfter, int amount)
    {
        var before = (int)Math.Round(PitcherAttributes.VelocityKmhFromLevel(levelAfter - amount));
        var after = (int)Math.Round(PitcherAttributes.VelocityKmhFromLevel(levelAfter));
        return after - before;
    }

    /// <summary>
    /// 上昇量の文字列。球速は「＋2km/h」、km/h に丸めて 0 なら空文字（数字を出さない）。
    /// 球速以外は従来どおり内部値の「＋4」。<paramref name="player"/> は上がった後の状態。
    /// </summary>
    public static string Amount(DevelopingPlayer player, AbilityGain gain)
    {
        if (gain.Ability != AbilityKind.Velocity) return "＋" + gain.Amount;
        var kmh = VelocityGainKmh(player.Level(AbilityKind.Velocity), gain.Amount);
        return kmh > 0 ? "＋" + kmh + "km/h" : "";
    }
}

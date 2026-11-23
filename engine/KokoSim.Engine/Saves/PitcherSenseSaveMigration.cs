using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 投手センス（issue #1472・v44）のセーブ移行。v43 以前のセーブの選手は投手センスを持っていないので、
/// 生成（<see cref="ProspectGenerator"/>／<see cref="Nation.StrengthTeamFactory"/>）と同じ分布の形から決定論的に与える:
/// 野手水準（総合力×<see cref="RosterCoefficients.NonPitcherSenseFactor"/>）↔ 投手上振れ（総合力＋
/// <see cref="RosterCoefficients.PitcherSenseBonus"/>）を投手寄せ重み w（守備位置適性から）で補間し、σ
/// <see cref="RosterCoefficients.PitcherSenseSd"/> の個体差を足す。
///
/// <para>総合力は生成時の値が残っていないので、今の能力から近似する（打撃系の平均と投手系の平均の高い方・物理Lv）。
/// 個体差の乱数は「セーブのマスターシード×選手の識別子」から作る独立ストリーム＝同じセーブは何度読んでも同じ値
/// （不変条件#2。Guid・System.Random は使わない）。</para>
///
/// <para>能力の物差しの移行（<see cref="AbilityScaleSaveMigration"/>）の<b>後</b>に呼ぶ＝表示Lvで書き込む。</para>
/// </summary>
public static class PitcherSenseSaveMigration
{
    /// <summary>投手センスが入った版。</summary>
    public const int IntroducedVersion = 44;

    private const ulong OwnSalt = 0x5E45_5A7E_0000_0001UL;
    private const ulong AiSalt = 0x5E45_5A7E_0000_0002UL;

    public static void Apply(SaveState s) => Apply(s, RosterCoefficients.Default);

    public static void Apply(SaveState s, RosterCoefficients rc)
    {
        var root = new Xoshiro256Random(s.MasterSeed ^ OwnSalt);
        foreach (var p in s.Roster) Fill(p, root, 0UL, rc);
        if (s.Incoming is { } inc)
            foreach (var p in inc.Players) Fill(p, root, 1UL, rc);
        if (s.Scouting is { } sc)
            foreach (var c in sc.Candidates) Fill(c.Player, root, 2UL, rc);

        var aiRoot = new Xoshiro256Random(s.MasterSeed ^ AiSalt);
        foreach (var roster in s.NationRosters)
            foreach (var ai in roster.Players)
            {
                var snap = ai.Snapshot;
                var apt = new int[ai.Aptitudes.Count];
                for (var i = 0; i < apt.Length; i++) apt[i] = ai.Aptitudes[i];
                var w = PitcherRoleResolver.PitcherWeight(apt[(int)FieldPosition.Pitcher], AptitudeRoller.BestField(apt), rc);
                var total = Total(snap.Contact, snap.Power, snap.Fielding, snap.Catching,
                    snap.Pitching.Control, snap.Pitching.PitchRank);
                var rng = aiRoot.Fork(((ulong)(uint)roster.SchoolId << 32) ^ (uint)ai.Id);
                ai.Snapshot = snap with { PitcherSense = Sample(total, w, rc, rng) };
            }
    }

    /// <summary>育成選手1人に投手センスを与える（表示Lv）。<paramref name="group"/> は自校・入部予定・スカウト候補の区別（ID が重なりうる）。</summary>
    internal static void Fill(DevelopingPlayer p, IRandomSource root, ulong group, RosterCoefficients rc)
    {
        var w = PitcherRoleResolver.PitcherWeight(
            p.Aptitude(FieldPosition.Pitcher), PitcherRoleResolver.BestFieldAptitude(p), rc);
        var total = Total(p.Level(AbilityKind.Contact), p.Level(AbilityKind.Power), p.Level(AbilityKind.Fielding),
            p.Level(AbilityKind.Catching), p.Level(AbilityKind.Control), p.Level(AbilityKind.PitchRank));
        p.PitcherSense = Sample(total, w, rc, root.Fork((group << 40) ^ (uint)p.Id));
        p.PitcherSenseReading = null;
    }

    /// <summary>総合力の近似（物理Lv）: 打撃・守備系の平均と投手系の平均の高い方。</summary>
    private static double Total(int contact, int power, int fielding, int catching, int control, int pitchRank)
    {
        var bat = (contact + power + fielding + catching) / 4.0;
        var pit = (control + pitchRank) / 2.0;
        return AbilityScale.Phys(Math.Max(bat, pit));
    }

    /// <summary>生成と同じ形の投手センス（物理Lvで引いて表示Lvで返す）。</summary>
    private static int Sample(double total, double w, RosterCoefficients rc, IRandomSource rng)
    {
        var low = total * rc.NonPitcherSenseFactor;
        var mean = low + (total + rc.PitcherSenseBonus - low) * w;
        var phys = (int)Math.Round(MathUtil.Clamp(mean + rng.NextGaussian(0, rc.PitcherSenseSd), 5, 95));
        return AbilityScale.Disp(phys);
    }
}

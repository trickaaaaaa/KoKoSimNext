using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 背番号どおりの守備位置で組んだスタメンの打順を、AI と同じ規則で並べる純関数（issue #955）。
/// 守備位置は呼び手が背番号から決めた値を動かさず、並べるのは打順だけ。
/// 規則は <see cref="LineupOrderer.Arrange"/>（1番出塁＋走力・2番小技・4番最強打者）と、
/// 投手の席は <see cref="LineupOrderer.BatsWithFielders"/>（打てる投手だけ野手と並べ、それ以外は9番）。
/// 乱数不使用・UnityEngine 非依存。
/// </summary>
public static class DefaultLineupOrder
{
    /// <summary>
    /// 8野手（守備位置つき）と投手を打順に並べ、9枠の（ロスターindex, 守備位置）を打順順で返す。
    /// </summary>
    public static IReadOnlyList<(int RosterIndex, FieldPosition Position)> Arrange(
        IReadOnlyList<DevelopingPlayer> roster,
        IReadOnlyList<(int RosterIndex, FieldPosition Position)> fielders,
        int pitcherRosterIndex,
        LineupCoefficients? coeff = null)
    {
        if (fielders.Count != 8) throw new ArgumentException("野手は8人でなければならない", nameof(fielders));
        var c = coeff ?? new LineupCoefficients();

        var fielderPlayers = fielders.Select(f => RosterTeamBuilder.ToPlayer(roster[f.RosterIndex], f.Position)).ToList();
        var pitcher = RosterTeamBuilder.ToPlayer(roster[pitcherRosterIndex], FieldPosition.Pitcher);

        var all = fielderPlayers.Append(pitcher).ToList();
        var slots = fielders.Append((pitcherRosterIndex, FieldPosition.Pitcher)).ToList();

        // Arrange は渡した Player インスタンスをそのまま並べ替えて返す＝参照で元の枠へ引き戻す。
        IReadOnlyList<Player> ordered = LineupOrderer.BatsWithFielders(pitcher, fielderPlayers, c)
            ? LineupOrderer.Arrange(all, c)
            : LineupOrderer.Arrange(fielderPlayers, c).Append(pitcher).ToList();

        return ordered.Select(p => slots[all.FindIndex(x => ReferenceEquals(x, p))]).ToList();
    }
}

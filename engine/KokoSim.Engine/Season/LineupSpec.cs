using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Engine.Season;

/// <summary>打順1枠＝出場選手＋守備位置。</summary>
public sealed record LineupSlot(DevelopingPlayer Player, FieldPosition Position);

/// <summary>
/// 試合前スタメン（打順9人＋守備位置・DH・先発投手・控え）。UI が編集して <see cref="RosterTeamBuilder.BuildFromLineup"/>
/// で試合用 <see cref="Match.Game.Team"/> へ組む。UnityEngine 非依存の純データ（不変条件#3）。
/// DhSlot ≧ 0 のとき該当打順が指名打者で、投手は打順外（StartingPitcher が必須）。
/// </summary>
public sealed record LineupSpec(
    IReadOnlyList<LineupSlot> BattingOrder,
    int PitcherSlot = 8,
    int DhSlot = -1,
    DevelopingPlayer? StartingPitcher = null,
    IReadOnlyList<DevelopingPlayer>? Bullpen = null,
    IReadOnlyList<DevelopingPlayer>? Bench = null,
    string Name = "自校",
    ITacticsBrain? Tactics = null)
{
    /// <summary>DH制か。</summary>
    public bool UsesDh => DhSlot >= 0;
}

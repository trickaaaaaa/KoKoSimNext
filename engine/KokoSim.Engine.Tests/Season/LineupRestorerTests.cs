using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 前回スタメンの復元（<see cref="LineupRestorer"/>・issue lineup-carry-over-previous-game）。
/// スタメン設定画面の既定を「前の試合で確定したスタメン」から始めるための純関数を固定する。
/// </summary>
public sealed class LineupRestorerTests
{
    private static readonly FieldPosition[] NonDh =
    {
        FieldPosition.CenterField, FieldPosition.SecondBase, FieldPosition.Shortstop, FieldPosition.FirstBase,
        FieldPosition.LeftField, FieldPosition.ThirdBase, FieldPosition.Catcher, FieldPosition.RightField,
        FieldPosition.Pitcher,
    };

    // ロスター12人（Id 1..12・index=Id-1）。全員ベンチ入り。打撃力順は Id 昇順、投手力順は Id 降順で渡す
    // （どちらが穴埋めに使われたかを結果の Id で区別できる）。
    private static List<DevelopingPlayer> Roster(int n = 12)
    {
        var list = new List<DevelopingPlayer>();
        for (var i = 1; i <= n; i++) list.Add(new DevelopingPlayer { Name = $"P{i}", Id = i, UniformNumber = i });
        return list;
    }

    private static LineupRestoreResult Run(LineupMemory? m, IReadOnlyList<DevelopingPlayer> roster,
        System.Func<int, bool>? eligible = null, bool dhAllowed = true)
    {
        var batting = Enumerable.Range(0, roster.Count).ToList();                 // 打撃力順＝index 昇順
        var pitching = Enumerable.Range(0, roster.Count).Reverse().ToList();      // 投手力順＝index 降順
        return LineupRestorer.Restore(m, roster, eligible ?? (_ => true), batting, pitching, dhAllowed);
    }

    private static LineupMemory NonDhMemory() => new(new[] { 3, 5, 7, 9, 11, 2, 4, 6, 1 }, NonDh, null);

    [Fact]
    public void AllPresent_RestoresExactly()
    {
        var roster = Roster();
        var r = Run(NonDhMemory(), roster);

        Assert.True(r.Restored);
        Assert.Equal(new[] { 2, 4, 6, 8, 10, 1, 3, 5, 0 }, r.RosterIndices);   // index = Id-1
        Assert.Equal(NonDh, r.Positions);
        Assert.False(r.UsesDh);
        Assert.Equal(-1, r.StartingPitcherIndex);
    }

    [Fact]
    public void OneRetired_FillsOnlyThatSlot_OthersUnchanged()
    {
        // Id=7（3番・遊撃）が引退＝ロスターに居ない。他8枠は不変、3番の穴は打撃力順で未使用の先頭（Id=8）。
        var roster = Roster().Where(p => p.Id != 7).ToList();
        var r = Run(NonDhMemory(), roster);

        Assert.True(r.Restored);
        var ids = r.RosterIndices.Select(i => roster[i].Id).ToArray();
        Assert.Equal(new[] { 3, 5, 8, 9, 11, 2, 4, 6, 1 }, ids);
        Assert.Equal(FieldPosition.Shortstop, r.Positions[2]);   // 守備位置は枠のまま
    }

    [Fact]
    public void PitcherSlotHole_IsFilledByPitchingOrder()
    {
        // 9番（投手）の Id=1 がベンチ外。投手枠の穴は投手力順（index 降順＝Id=12）で埋まる。
        var roster = Roster();
        var r = Run(NonDhMemory(), roster, eligible: i => roster[i].Id != 1);

        Assert.True(r.Restored);
        Assert.Equal(12, roster[r.RosterIndices[8]].Id);
        Assert.Equal(FieldPosition.Pitcher, r.Positions[8]);
    }

    [Fact]
    public void BenchedOut_IsDropped()
    {
        // Id=5（2番）が背番号を外された（ベンチ外）＝枠から外れて穴埋め。他は不変。
        var roster = Roster();
        var r = Run(NonDhMemory(), roster, eligible: i => roster[i].Id != 5);

        Assert.True(r.Restored);
        var ids = r.RosterIndices.Select(i => roster[i].Id).ToArray();
        Assert.Equal(8, ids[1]);
        Assert.Equal(new[] { 3, 8, 7, 9, 11, 2, 4, 6, 1 }, ids);
    }

    private static LineupMemory DhMemory()
    {
        var pos = (FieldPosition[])NonDh.Clone();
        pos[8] = FieldPosition.DesignatedHitter;      // 9番＝指、投手は打順外
        return new LineupMemory(new[] { 3, 5, 7, 9, 11, 2, 4, 6, 10 }, pos, StartingPitcherId: 1);
    }

    [Fact]
    public void Dh_RestoresStartingPitcher()
    {
        var roster = Roster();
        var r = Run(DhMemory(), roster);

        Assert.True(r.Restored);
        Assert.True(r.UsesDh);
        Assert.Equal(1, roster[r.StartingPitcherIndex].Id);
        Assert.Equal(FieldPosition.DesignatedHitter, r.Positions[8]);
        Assert.Equal(10, roster[r.RosterIndices[8]].Id);
    }

    [Fact]
    public void Dh_StartingPitcherGone_FilledByPitchingOrder()
    {
        var roster = Roster().Where(p => p.Id != 1).ToList();
        var r = Run(DhMemory(), roster);

        Assert.True(r.Restored);
        Assert.True(r.UsesDh);
        Assert.Equal(12, roster[r.StartingPitcherIndex].Id);
        Assert.DoesNotContain(r.StartingPitcherIndex, r.RosterIndices);
    }

    [Fact]
    public void Dh_NotAllowed_FoldsPitcherBackIntoDhSlot()
    {
        // DH不可: 「指」の枠に先発投手（Id=1）が戻り、DHの打者（Id=10）は外れる。
        var roster = Roster();
        var r = Run(DhMemory(), roster, dhAllowed: false);

        Assert.True(r.Restored);
        Assert.False(r.UsesDh);
        Assert.Equal(-1, r.StartingPitcherIndex);
        Assert.Equal(FieldPosition.Pitcher, r.Positions[8]);
        Assert.Equal(1, roster[r.RosterIndices[8]].Id);
        Assert.DoesNotContain(10, r.RosterIndices.Select(i => roster[i].Id));
    }

    [Fact]
    public void BrokenPositions_FallsBackToSeed()
    {
        // 守備網羅が崩れた記憶（中堅が2枠・遊撃なし）＝復元しない（engine の BuildFromLineup が弾く形を画面側で先に塞ぐ）。
        var pos = (FieldPosition[])NonDh.Clone();
        pos[2] = FieldPosition.CenterField;
        var m = new LineupMemory(new[] { 3, 5, 7, 9, 11, 2, 4, 6, 1 }, pos, null);
        Assert.False(Run(m, Roster()).Restored);
    }

    [Fact]
    public void NotEnoughEligible_FallsBackToSeed()
    {
        // ベンチ入りが8人しか居ない＝穴が埋まらない。
        var roster = Roster();
        var r = Run(NonDhMemory(), roster, eligible: i => roster[i].Id <= 8);
        Assert.False(r.Restored);
    }

    [Fact]
    public void NullOrMalformed_NotRestored()
    {
        Assert.False(Run(null, Roster()).Restored);
        Assert.False(Run(new LineupMemory(new[] { 1, 2, 3 }, NonDh, null), Roster()).Restored);
        // DH制なのに先発IDなし
        var pos = (FieldPosition[])NonDh.Clone(); pos[8] = FieldPosition.DesignatedHitter;
        Assert.False(Run(new LineupMemory(new[] { 3, 5, 7, 9, 11, 2, 4, 6, 10 }, pos, null), Roster()).Restored);
    }

    [Fact]
    public void FromSpec_ReadsDhSlotAsDesignatedHitter()
    {
        var roster = Roster();
        var order = new List<LineupSlot>();
        for (var i = 0; i < 9; i++)
            order.Add(new LineupSlot(roster[i + 1], i == 8 ? FieldPosition.Pitcher : NonDh[i]));   // UI の規約: DH枠は投手を渡す
        var spec = new LineupSpec(order, PitcherSlot: 8, DhSlot: 8, StartingPitcher: roster[0]);

        var m = LineupMemory.FromSpec(spec);

        Assert.True(m.UsesDh);
        Assert.Equal(8, m.DhSlot);
        Assert.Equal(FieldPosition.DesignatedHitter, m.Positions[8]);
        Assert.Equal(1, m.StartingPitcherId);
        Assert.Equal(Enumerable.Range(2, 9), m.PlayerIds);
        Assert.True(m.IsWellFormed);
    }
}

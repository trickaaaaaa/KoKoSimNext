using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 背番号割当（設計書06 §3.3b）の検証。自動割当が総合力上位20名へ一意な1〜20を振ること、
/// 自由割当が重複を退避すること、範囲ガード、決定論（同一ロスターで同結果）を確認する。
/// </summary>
public sealed class UniformNumberAssignerTests
{
    // 中核能力を一律 level で埋めた選手（AverageLevel を制御して順位を作る）。
    private static DevelopingPlayer Guy(string name, int level, bool pitcher = false)
    {
        var p = new DevelopingPlayer { Name = name };
        PitcherRoleResolver.SeedAptitudeForRole(p, pitcher);
        foreach (var k in AbilityKinds.All) { p.SetCap(k, 99); p.SetLevel(k, level); }
        return p;
    }

    // level 降順のロスター（25名）。index0 が最強。
    private static List<DevelopingPlayer> Roster(int count = 25)
        => Enumerable.Range(0, count).Select(i => Guy($"P{i:00}", 90 - i)).ToList();

    [Fact]
    public void AutoAssign_gives_unique_1to20_to_top20_and_zero_to_rest()
    {
        var roster = Roster(25);

        UniformNumberAssigner.AutoAssign(roster);

        // 上位20名（level 90..71）に 1..20 が昇順で付く（index順＝level降順）。
        for (var i = 0; i < 20; i++)
            Assert.Equal(i + 1, roster[i].UniformNumber);
        // 残り5名はベンチ外。
        for (var i = 20; i < 25; i++)
            Assert.Equal(0, roster[i].UniformNumber);

        Assert.True(UniformNumberAssigner.Validate(roster));
        // 1..20 が過不足なく一意に存在する。
        var assigned = roster.Select(p => p.UniformNumber).Where(n => n != 0).OrderBy(n => n);
        Assert.Equal(Enumerable.Range(1, 20), assigned);
    }

    [Fact]
    public void AutoAssign_ties_keep_roster_order_deterministically()
    {
        // 全員同 level（同点）。安定ソートで元の順＝index順に 1..20 が付く。
        var roster = Enumerable.Range(0, 22).Select(i => Guy($"P{i:00}", 50)).ToList();

        UniformNumberAssigner.AutoAssign(roster);

        for (var i = 0; i < 20; i++)
            Assert.Equal(i + 1, roster[i].UniformNumber);
        Assert.Equal(0, roster[20].UniformNumber);
        Assert.Equal(0, roster[21].UniformNumber);
    }

    [Fact]
    public void Assign_evicts_the_previous_holder_of_the_same_number()
    {
        var roster = Roster(25);
        UniformNumberAssigner.AutoAssign(roster);
        var holder = roster[0];         // 現在の背番号1
        var challenger = roster[24];    // 現在のベンチ外
        Assert.Equal(1, holder.UniformNumber);
        Assert.Equal(0, challenger.UniformNumber);

        UniformNumberAssigner.Assign(roster, challenger, 1);

        Assert.Equal(1, challenger.UniformNumber);
        Assert.Equal(0, holder.UniformNumber);           // 前の1番はベンチ外へ退避
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void Assign_releases_players_own_previous_number()
    {
        var roster = Roster(25);
        UniformNumberAssigner.AutoAssign(roster);
        var player = roster[4];         // 現在の背番号5
        Assert.Equal(5, player.UniformNumber);

        // ベンチ外の背番号22...は無いので、いったん空いている番号を作る: 20番を別選手からClearして空ける。
        UniformNumberAssigner.Clear(roster[19]);          // 20番を解放
        UniformNumberAssigner.Assign(roster, player, 20);

        Assert.Equal(20, player.UniformNumber);
        // 元の5番は誰も持っていない（player が手放した）。
        Assert.DoesNotContain(roster, p => p.UniformNumber == 5);
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void Place_swaps_with_the_holder_of_the_target_number()
    {
        var roster = Roster(25);
        UniformNumberAssigner.AutoAssign(roster);
        var p = roster[4];   // 背番号5
        var q = roster[5];   // 背番号6
        Assert.Equal(5, p.UniformNumber);
        Assert.Equal(6, q.UniformNumber);

        UniformNumberAssigner.Place(roster, p, 6);

        Assert.Equal(6, p.UniformNumber);
        Assert.Equal(5, q.UniformNumber);   // 元6番は p の元5番へ交換（退避でなく入替）
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void Place_into_empty_number_moves_and_vacates_old()
    {
        var roster = Roster(25);
        UniformNumberAssigner.AutoAssign(roster);
        var p = roster[4];   // 背番号5
        UniformNumberAssigner.Clear(roster[14]);   // 15番を空ける

        UniformNumberAssigner.Place(roster, p, 15);

        Assert.Equal(15, p.UniformNumber);
        Assert.DoesNotContain(roster, x => x.UniformNumber == 5);   // 元5番は空く
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void SwapPlayers_exchanges_numbers_including_bench_out()
    {
        var roster = Roster(25);
        UniformNumberAssigner.AutoAssign(roster);
        var starter = roster[0];     // 背番号1
        var benchOut = roster[24];   // ベンチ外0
        Assert.Equal(1, starter.UniformNumber);
        Assert.Equal(0, benchOut.UniformNumber);

        UniformNumberAssigner.SwapPlayers(starter, benchOut);

        Assert.Equal(0, starter.UniformNumber);
        Assert.Equal(1, benchOut.UniformNumber);
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void Clear_sets_bench_out()
    {
        var roster = Roster(25);
        UniformNumberAssigner.AutoAssign(roster);
        var player = roster[3];
        Assert.NotEqual(0, player.UniformNumber);

        UniformNumberAssigner.Clear(player);

        Assert.Equal(0, player.UniformNumber);
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void Assign_out_of_range_throws(int number)
    {
        var roster = Roster(25);
        Assert.Throws<ArgumentOutOfRangeException>(() => UniformNumberAssigner.Assign(roster, roster[0], number));
    }

    [Fact]
    public void Validate_detects_duplicate_numbers()
    {
        var roster = Roster(3);
        roster[0].UniformNumber = 7;
        roster[1].UniformNumber = 7;   // 重複
        roster[2].UniformNumber = 0;

        Assert.False(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void AutoAssign_is_deterministic_across_equal_rosters()
    {
        var a = Roster(25);
        var b = Roster(25);

        UniformNumberAssigner.AutoAssign(a);
        UniformNumberAssigner.AutoAssign(b);

        Assert.Equal(a.Select(p => p.UniformNumber), b.Select(p => p.UniformNumber));
    }

    [Fact]
    public void FillVacancies_FillsGapsWithBenchOutPlayersByLevelDescending_KeepsExistingAssignments()
    {
        var roster = Roster(25);
        UniformNumberAssigner.AutoAssign(roster);
        // 上位20名(index0..19)が1..20、下位5名(index20..24)がベンチ外。
        // 3つの穴を作る: 1番・10番・20番を持つ選手をベンチ外へ退避。
        UniformNumberAssigner.Clear(roster[0]);    // 1番が空く
        UniformNumberAssigner.Clear(roster[9]);    // 10番が空く
        UniformNumberAssigner.Clear(roster[19]);   // 20番が空く
        // ベンチ外は roster[0], roster[9], roster[19]（元1,10,20番）と roster[20..24]（level 70..66）。
        // level降順: roster[0](90) > roster[9](81) > roster[19](71) > roster[20](70) > ... > roster[24](66)。

        UniformNumberAssigner.FillVacancies(roster);

        // 空いた3枠(1,10,20)を level 上位3名で埋める＝小さい番号から強い順。
        Assert.Equal(1, roster[0].UniformNumber);
        Assert.Equal(10, roster[9].UniformNumber);
        Assert.Equal(20, roster[19].UniformNumber);
        // 既存の他の割当(2..9, 11..19)は一切変わっていない。
        for (var i = 1; i < 9; i++) Assert.Equal(i + 1, roster[i].UniformNumber);
        for (var i = 10; i < 19; i++) Assert.Equal(i + 1, roster[i].UniformNumber);
        // 埋めきれなかった下位2名(roster[23], roster[24])はベンチ外のまま。
        Assert.Equal(0, roster[23].UniformNumber);
        Assert.Equal(0, roster[24].UniformNumber);
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void FillVacancies_StopsWhenBenchOutIsExhausted_LeavesRemainingSlotsVacant()
    {
        // 20人ちょうど、うち2名だけベンチ外(番号0)。空き番号は 2 つ用意する。
        var roster = Roster(20);
        UniformNumberAssigner.AutoAssign(roster);
        UniformNumberAssigner.Clear(roster[5]);   // 6番が空く
        UniformNumberAssigner.Clear(roster[15]);  // 16番が空く
        // ベンチ外は roster[5]と roster[15]のみ(2名)。空き番号も6,16の2つ＝過不足なく埋まる。

        UniformNumberAssigner.FillVacancies(roster);

        Assert.DoesNotContain(roster, p => p.UniformNumber == 0);
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void FillVacancies_TiesKeepRosterOrderDeterministically()
    {
        // 18人に既存番号1..18を割当済み。残り4人は同levelでベンチ外。空き番号は19,20の2つだけ。
        var roster = new List<DevelopingPlayer>();
        for (var i = 0; i < 18; i++)
        {
            var p = Guy($"A{i:00}", 80);
            p.UniformNumber = i + 1;
            roster.Add(p);
        }
        for (var i = 0; i < 4; i++)
            roster.Add(Guy($"B{i:00}", 50));   // 同level・ベンチ外(既定0)

        UniformNumberAssigner.FillVacancies(roster);

        // 空き番号は19,20の2つ。同点は元のロスター順＝先頭2名(B00,B01)が小さい番号から埋める。
        Assert.Equal(19, roster[18].UniformNumber);
        Assert.Equal(20, roster[19].UniformNumber);
        Assert.Equal(0, roster[20].UniformNumber);
        Assert.Equal(0, roster[21].UniformNumber);
        Assert.True(UniformNumberAssigner.Validate(roster));
    }

    [Fact]
    public void FillVacancies_NoVacancies_ChangesNothing()
    {
        var roster = Roster(20);
        UniformNumberAssigner.AutoAssign(roster);
        var before = roster.Select(p => p.UniformNumber).ToList();

        UniformNumberAssigner.FillVacancies(roster);

        Assert.Equal(before, roster.Select(p => p.UniformNumber));
    }

    [Fact]
    public void FillVacancies_NoBenchOutPlayers_ChangesNothing()
    {
        var roster = Roster(15);
        UniformNumberAssigner.AutoAssign(roster);   // 15人全員に1..15が付き、ベンチ外はいない
        var before = roster.Select(p => p.UniformNumber).ToList();

        UniformNumberAssigner.FillVacancies(roster);

        Assert.Equal(before, roster.Select(p => p.UniformNumber));
    }
}

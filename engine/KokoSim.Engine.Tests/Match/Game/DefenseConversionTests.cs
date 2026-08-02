using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 試合中の守備位置の自由な扱い（issue #235・設計書09 §2/§6）。
/// ①守備位置の入替＝コンバートは<b>交代回数を消費しない</b>、②守備交代＋入替で入る選手を任意位置へ、
/// ③コンバート継投（元投手は退場せず守備へ・控え投手が登板）、④コンバートを挟んでもスタミナは回復しない。
/// </summary>
public sealed class DefenseConversionTests
{
    private static Player At(FieldPosition pos, string name, int fielding = 50)
        => new() { Position = pos, Name = name, Contact = 50, Power = 50, Fielding = fielding };

    private static Player Rp(string name) => At(FieldPosition.Pitcher, name) with { Pitching = PitcherAttributes.LeagueAverage };

    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher,
    };

    private const int LeftSlot = 5;      // 左翼手の打順スロット
    private const int PitcherSlot = 8;

    /// <summary>非DH制のチーム。bullpen/bench は明示（既定は控えなし＝path①を通したいとき用）。</summary>
    private static Team MakeTeam(IEnumerable<Player>? bullpen = null, IEnumerable<Player>? bench = null)
    {
        var order = Positions.Select((p, i) => p == FieldPosition.Pitcher
            ? Rp("先発P")
            : At(p, $"{p}先発")).ToList();
        return new Team
        {
            Name = "テスト校",
            BattingOrder = order,
            PitcherSlot = PitcherSlot,
            Reserves = KokoSim.Engine.Match.Game.TeamReserves.Merge(bullpen?.ToList() ?? new List<Player>(), bench?.ToList() ?? new List<Player>()),
        };
    }

    /// <summary>DH制のチーム（打順9人＝野手8＋DH、投手は打順外）。</summary>
    private static Team MakeDhTeam(IEnumerable<Player>? bullpen = null)
    {
        var order = Positions.Take(8).Select(p => At(p, $"{p}先発")).ToList();
        order.Add(At(FieldPosition.Catcher, "DH"));   // DH枠の守備位置は表示専用（守備に就かない）
        return new Team
        {
            Name = "テスト校",
            BattingOrder = order,
            DhSlot = 8,
            StartingPitcher = Rp("先発P"),
            Reserves = KokoSim.Engine.Match.Game.TeamReserves.Merge(bullpen?.ToList() ?? new List<Player>(), new List<Player>()),
        };
    }

    private static IReadOnlyList<FieldPosition> PosOf(TeamState s) => s.CurrentLineup.Select(p => p.Position).ToList();

    // ===== ① 守備位置の入替（コンバート）＝交代回数を消費しない =====

    [Fact]
    public void SwapDefensivePositions_SwapsPositions_WithoutConsumingSubstitution()
    {
        var state = new TeamState(MakeTeam());
        var catcherName = state.CurrentLineup[0].Name;
        var ssName = state.CurrentLineup[4].Name;

        Assert.True(state.SwapDefensivePositions(0, 4));

        Assert.Equal(FieldPosition.Shortstop, state.CurrentLineup[0].Position);
        Assert.Equal(FieldPosition.Catcher, state.CurrentLineup[4].Position);
        Assert.Equal(catcherName, state.CurrentLineup[0].Name);   // 選手は動かない（守備位置だけ動く）
        Assert.Equal(ssName, state.CurrentLineup[4].Name);
        // 交代ではない＝回数を消費せず、誰も退場しない（issue #235 方針①）。
        Assert.Equal(0, state.Substitutions);
        Assert.All(state.CurrentLineup, p => Assert.False(state.IsRetired(p)));
        Assert.Equal(9, PosOf(state).Distinct().Count());
    }

    [Fact]
    public void SwapDefensivePositions_RejectsPitcherSlot()
    {
        var state = new TeamState(MakeTeam());
        var before = PosOf(state);

        // 投手が動くのは「登板の変更」＝別コマンド（RelieveWith / ConvertPitcherAndRelieve）の担当。
        Assert.False(state.IsSwappableDefenseSlot(PitcherSlot));
        Assert.False(state.SwapDefensivePositions(0, PitcherSlot));
        Assert.Equal(before, PosOf(state));
    }

    [Theory]
    [InlineData(3, 3)]     // 同一スロット
    [InlineData(-1, 2)]    // 範囲外
    [InlineData(2, 9)]
    public void SwapDefensivePositions_SameSlotOrOutOfRange_IsNoOp(int a, int b)
    {
        var state = new TeamState(MakeTeam());
        var before = PosOf(state);

        Assert.False(state.SwapDefensivePositions(a, b));
        Assert.Equal(before, PosOf(state));
    }

    [Fact]
    public void SwapDefensivePositions_Dh_ExcludesDhSlotOnly()
    {
        var state = new TeamState(MakeDhTeam());

        Assert.False(state.IsSwappableDefenseSlot(8));   // 8番＝DH枠。守備に就かないので入替対象外
        Assert.False(state.SwapDefensivePositions(0, 8));
        // DHでは投手が打順外なので、DH枠以外の8枠はすべて野手＝すべて入れ替えられる。
        for (var i = 0; i < 8; i++) Assert.True(state.IsSwappableDefenseSlot(i));
        Assert.True(state.SwapDefensivePositions(1, 6));
        Assert.Equal(FieldPosition.CenterField, state.CurrentLineup[1].Position);
        Assert.Equal(0, state.Substitutions);
    }

    // ===== ② 守備交代で入る選手を任意の守備位置へ（交代→入替の合成） =====

    [Fact]
    public void DefensiveSubThenSwap_PutsIncomingPlayerAtAnyPosition()
    {
        var sub = At(FieldPosition.FirstBase, "守備固め", fielding: 80);
        var state = new TeamState(MakeTeam(bench: new[] { sub }));
        var pushedOut = state.CurrentLineup[4].Name;              // 遊撃手（押し出される既存野手）

        // 左翼の選手を控えと交代（守備位置は継承＝左翼）→ 遊撃と入れ替えて任意位置へ。
        Assert.True(state.DefensiveSub(state.CurrentLineup[LeftSlot], sub));
        Assert.True(state.SwapDefensivePositions(LeftSlot, 4));

        Assert.Equal("守備固め", state.CurrentLineup[LeftSlot].Name);
        Assert.Equal(FieldPosition.Shortstop, state.CurrentLineup[LeftSlot].Position);   // 入る選手が任意位置へ
        Assert.Equal(pushedOut, state.CurrentLineup[4].Name);
        Assert.Equal(FieldPosition.LeftField, state.CurrentLineup[4].Position);          // 押し出された野手が空き位置へ
        Assert.Equal(1, state.Substitutions);   // 消費するのは交代1回だけ（入替は無料）
        Assert.Equal(9, PosOf(state).Distinct().Count());
    }

    // ===== ③ コンバート継投 =====

    [Fact]
    public void ConvertPitcherAndRelieve_MovesPitcherToField_AndKeepsHimInGame()
    {
        var relief = Rp("二番手");
        var state = new TeamState(MakeTeam(bullpen: new[] { relief }));
        var starter = state.CurrentPitcher;
        var displaced = state.CurrentLineup[LeftSlot];

        Assert.True(state.ConvertPitcherAndRelieve(FieldPosition.LeftField, relief));

        // 元投手は退場せず、自分の打順スロットに残ったまま左翼へ回る。
        Assert.Equal(starter.Name, state.CurrentLineup[PitcherSlot].Name);
        Assert.Equal(FieldPosition.LeftField, state.CurrentLineup[PitcherSlot].Position);
        Assert.False(state.IsRetired(starter));
        // 押し出された左翼手は退場し、その打順スロットへ新投手が入る。
        Assert.True(state.IsRetired(displaced));
        Assert.Equal("二番手", state.CurrentLineup[LeftSlot].Name);
        Assert.Equal(FieldPosition.Pitcher, state.CurrentLineup[LeftSlot].Position);
        Assert.Equal(LeftSlot, state.PitcherSlot);
        Assert.Equal("二番手", state.CurrentPitcher.Name);
        // 野手1人の出入りがあるので交代1回・継投1回。守備は9ポジション網羅のまま。
        Assert.Equal(1, state.Substitutions);
        Assert.Equal(1, state.PitcherChanges);
        Assert.Equal(9, PosOf(state).Distinct().Count());
        Assert.Equal(PitchingChangeKind.PitcherConvertedRelief, state.LastPitchingChange!.Value.Kind);
        Assert.Equal(FieldPosition.LeftField, state.LastPitchingChange!.Value.BackfillPosition);
    }

    [Fact]
    public void ConvertPitcherAndRelieve_Dh_KeepsDhAndPutsPitcherIntoOrder()
    {
        var relief = Rp("二番手");
        var state = new TeamState(MakeDhTeam(bullpen: new[] { relief }));
        var starter = state.CurrentPitcher;
        var displaced = state.CurrentLineup[LeftSlot];

        Assert.True(state.ConvertPitcherAndRelieve(FieldPosition.LeftField, relief));

        // DH制では投手が打順外なので、元投手は押し出された野手の打順スロットを引き継ぐ。
        Assert.Equal(starter.Name, state.CurrentLineup[LeftSlot].Name);
        Assert.Equal(FieldPosition.LeftField, state.CurrentLineup[LeftSlot].Position);
        Assert.True(state.IsRetired(displaced));
        Assert.False(state.IsRetired(starter));
        Assert.Equal("二番手", state.CurrentPitcher.Name);
        Assert.DoesNotContain(state.CurrentLineup, p => p.Position == FieldPosition.Pitcher);   // 新投手は打順外
        Assert.True(state.UsesDh);                                                             // DHは維持
        Assert.Equal(1, state.Substitutions);
    }

    [Fact]
    public void ConvertPitcherAndRelieve_RejectsPitcherAndDhTargets()
    {
        var relief = Rp("二番手");
        var state = new TeamState(MakeTeam(bullpen: new[] { relief }));

        Assert.False(state.ConvertPitcherAndRelieve(FieldPosition.Pitcher, relief));
        Assert.False(state.ConvertPitcherAndRelieve(FieldPosition.DesignatedHitter, relief));
        Assert.Equal(0, state.PitcherChanges);
        Assert.Contains(relief, state.AvailableReserves);   // 候補は消費されない
    }

    // ===== ④ スティント疲労は選手単位（コンバートを挟んでも回復しない） =====

    [Fact]
    public void ConvertedPitcher_ResumesHisOwnPitchCountWhenHeReturnsToMound()
    {
        var relief = Rp("二番手");
        // ベンチ空＝在フィールド野手の登板は path①（旧投手も残留）になる。
        var state = new TeamState(MakeTeam(bullpen: new[] { relief }));
        state.AddPitches(72, 1.25);
        var beforePitches = state.PitchesThrown;
        var beforeFatigue = state.FatiguePitches;
        Assert.Equal(72, beforePitches);

        Assert.True(state.ConvertPitcherAndRelieve(FieldPosition.LeftField, relief));
        Assert.Equal(0, state.PitchesThrown);          // 新投手は自分の球数（初登板なので0）から
        state.AddPitches(15);

        // 左翼に回っていた元投手を再登板させる（在フィールド野手の登板＝RelieveWith）。
        Assert.True(state.RelieveWith(state.CurrentLineup[PitcherSlot]));

        Assert.Equal(beforePitches, state.PitchesThrown);   // 挟んだだけでスタミナは回復しない
        Assert.Equal(beforeFatigue, state.FatiguePitches);
    }

    [Fact]
    public void RetiredPitcher_StintCarryOver_DoesNotChangeOrdinaryRelief()
    {
        // 通常継投（旧投手は退場して戻れない）は従来どおり新投手が0から始まる。
        var state = new TeamState(MakeTeam(bullpen: new[] { Rp("二番手"), Rp("三番手") }));
        state.AddPitches(90);

        Assert.True(state.ChangePitcherTo(state.AvailableReserves[0]));
        Assert.Equal(0, state.PitchesThrown);
        Assert.Equal(0.0, state.FatiguePitches);
    }
}

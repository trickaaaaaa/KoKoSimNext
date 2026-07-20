using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 選手交代メカニズム（設計書09 §6, C-1）。代打・代走・守備交代・DH解除を TeamState 直接で検証。
/// 高校野球ルール＝リエントリー禁止（退いた選手は再出場不可）。控え空＝無交代＝従来挙動。
/// </summary>
public sealed class SubstitutionTests
{
    private static Player At(FieldPosition pos, string name, int contact = 50)
        => new() { Position = pos, Name = name, Contact = contact };

    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher,
    };

    private static Team MakeTeam(IEnumerable<Player>? bench = null)
    {
        var order = Positions.Select((p, i) => p == FieldPosition.Pitcher
            ? At(p, "先発P") with { Pitching = PitcherAttributes.LeagueAverage }
            : At(p, $"{p}先発", contact: 40 + i)).ToList();
        return new Team
        {
            Name = "テスト校",
            BattingOrder = order,
            PitcherSlot = 8,
            Bench = bench?.ToList() ?? new List<Player>(),
        };
    }

    // ===== 代打 =====

    [Fact]
    public void PinchHitNext_ReplacesUpcomingBatter_AndRetiresStarter()
    {
        var sub = At(FieldPosition.LeftField, "代打マン", contact: 90);
        var state = new TeamState(MakeTeam(new[] { sub }));

        var starter = state.PeekBatter();            // 打順先頭（Catcher先発）
        Assert.True(state.PinchHitNext(sub));

        Assert.Equal("代打マン", state.PeekBatter().Name);          // 次打者が代打に
        Assert.Equal(starter.Position, state.PeekBatter().Position); // 守備位置を継承（捕手）
        Assert.Equal(FieldPosition.Catcher, state.PeekBatter().Position);
        Assert.Equal("代打マン", state.NextBatter().Name);           // 実際に代打が打席へ
        Assert.Equal(1, state.Substitutions);
        Assert.DoesNotContain(sub, state.Bench);                    // 控えから消費
        Assert.False(state.IsAvailable(sub));
    }

    [Fact]
    public void EmptyBench_SubstitutionIsNoOp()
    {
        var state = new TeamState(MakeTeam());
        var before = state.CurrentLineup.ToList();

        Assert.False(state.PinchHitNext(At(FieldPosition.LeftField, "部外者")));
        Assert.Equal(0, state.Substitutions);
        Assert.Equal(before, state.CurrentLineup);                  // ラインナップ不変
    }

    [Fact]
    public void RetiredStarter_CannotReenter_AndBenchPlayerUsedOnce()
    {
        var sub = At(FieldPosition.Catcher, "代打", contact: 88);
        var state = new TeamState(MakeTeam(new[] { sub }));
        var starter = state.PeekBatter();

        Assert.True(state.PinchHitNext(sub));
        // 退いた先発は控えにいない＝再出場不可。
        Assert.False(state.IsAvailable(starter));
        Assert.False(state.DefensiveSub(sub, starter));             // リエントリー拒否
        // 使い切った控えは二度使えない。
        Assert.False(state.PinchHitNext(sub));
        Assert.Equal(1, state.Substitutions);
    }

    // ===== 守備交代 =====

    [Fact]
    public void DefensiveSub_SwapsFielder_AndInheritsPosition_InAlignment()
    {
        var sub = At(FieldPosition.LeftField, "守備固め");
        var state = new TeamState(MakeTeam(new[] { sub }));
        var ss = state.CurrentLineup.First(p => p.Position == FieldPosition.Shortstop);

        Assert.True(state.DefensiveSub(ss, sub));

        var alignment = state.DefensiveAlignment(new FieldGeometry());
        // 遊撃の守備者が交代選手に置き換わっている（守備位置を継承）。
        Assert.Contains(alignment, f => f.Position == FieldPosition.Shortstop);
        Assert.Contains(state.CurrentLineup, p => p.Name == "守備固め" && p.Position == FieldPosition.Shortstop);
        Assert.DoesNotContain(ss, state.CurrentLineup);
    }

    // ===== 代走 =====

    [Fact]
    public void PinchRunFor_ReplacesRunnerInLineup()
    {
        var sub = At(FieldPosition.LeftField, "代走", contact: 30);
        var state = new TeamState(MakeTeam(new[] { sub }));
        var runner = state.CurrentLineup[2]; // 3番（SecondBase先発）が出塁したと仮定

        Assert.True(state.PinchRunFor(runner, sub));
        Assert.Equal("代走", state.CurrentLineup[2].Name);
        Assert.Equal(runner.Position, state.CurrentLineup[2].Position); // 守備位置継承
    }

    // ===== DH解除 =====

    private static Team MakeDhTeam(IEnumerable<Player>? bench = null)
    {
        // 8守備位置＋DH の打順9人、投手は打順外。DH の Position は守備配置で参照されない（スロットごとスキップ）。
        var fielders = Positions.Where(p => p != FieldPosition.Pitcher)
            .Select((p, i) => At(p, $"{p}先発", contact: 40 + i)).ToList();
        var order = new List<Player>(fielders) { At(FieldPosition.Catcher, "DH打者", contact: 95) };
        return new Team
        {
            Name = "DH校",
            BattingOrder = order,
            DhSlot = 8,
            StartingPitcher = At(FieldPosition.Pitcher, "先発P") with { Pitching = PitcherAttributes.LeagueAverage },
            Bench = bench?.ToList() ?? new List<Player>(),
        };
    }

    [Fact]
    public void ReleaseDh_PitcherEntersBattingOrder_AndDhVanishes()
    {
        var state = new TeamState(MakeDhTeam());
        Assert.True(state.UsesDh);

        Assert.True(state.ReleaseDh());
        Assert.False(state.UsesDh);
        // DHスロットに投手が入り打席に立つ。
        Assert.Equal(FieldPosition.Pitcher, state.CurrentLineup[8].Position);
        Assert.Equal(state.CurrentPitcher, state.CurrentLineup[8]);
        // 守備は9人（投手含む）で成立。
        var alignment = state.DefensiveAlignment(new FieldGeometry());
        Assert.Equal(9, alignment.Count);
    }

    [Fact]
    public void ReleaseDh_ToField_MovesDhOntoDefense()
    {
        var state = new TeamState(MakeDhTeam());
        var dh = state.CurrentLineup[8];
        var displaced = state.CurrentLineup.First(p => p.Position == FieldPosition.LeftField);

        Assert.True(state.ReleaseDh(FieldPosition.LeftField));
        Assert.False(state.UsesDh);
        // DHの選手が左翼を守り、元の左翼手は退場。
        Assert.Contains(state.CurrentLineup, p => p.Name == dh.Name && p.Position == FieldPosition.LeftField);
        Assert.DoesNotContain(displaced, state.CurrentLineup);
        var alignment = state.DefensiveAlignment(new FieldGeometry());
        Assert.Equal(9, alignment.Count);
    }

    [Fact]
    public void ReleaseDh_OnNonDhTeam_ReturnsFalse()
    {
        var state = new TeamState(MakeTeam());
        Assert.False(state.ReleaseDh());
    }
}

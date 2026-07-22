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

    // ===== DHスロットの表示位置（enum由来の「指」表記, issue #70） =====
    // Player.Position 自体は本来守備位置を内部保持（守備適性計算・ReleaseDh の引き継ぎに必須）。
    // 表示専用の BattingLine/LiveBatterSlot だけが DesignatedHitter を返す。

    [Fact]
    public void BuildBattingLines_DhSlot_ShowsDesignatedHitter_OthersKeepRealPosition()
    {
        var state = new TeamState(MakeDhTeam());
        var lines = state.BuildBattingLines();

        Assert.Equal(FieldPosition.DesignatedHitter, lines[8].Position);
        for (var i = 0; i < 8; i++) Assert.NotEqual(FieldPosition.DesignatedHitter, lines[i].Position);
        // 内部の Player.Position は本来守備位置のまま（守備適性計算に必要）。
        Assert.Equal(FieldPosition.Catcher, state.CurrentLineup[8].Position);
    }

    [Fact]
    public void LiveLineup_DhSlot_ShowsDesignatedHitter_AndRevertsAfterReleaseDh()
    {
        var state = new TeamState(MakeDhTeam());
        Assert.Equal(FieldPosition.DesignatedHitter, state.LiveLineup()[8].Position);

        Assert.True(state.ReleaseDh(FieldPosition.LeftField));
        // DH解除後はそのスロットに投手が入り、実守備位置（投手）を表示する。
        Assert.Equal(FieldPosition.Pitcher, state.LiveLineup()[8].Position);
    }

    [Fact]
    public void BuildBattingLines_AndLiveLineup_NonDhTeam_NeverShowDesignatedHitter()
    {
        var state = new TeamState(MakeTeam());
        Assert.DoesNotContain(state.BuildBattingLines(), l => l.Position == FieldPosition.DesignatedHitter);
        Assert.DoesNotContain(state.LiveLineup(), l => l.Position == FieldPosition.DesignatedHitter);
    }

    // ===== 投手交代（指名継投, issue #22 A） =====

    private static Player Rp(string name)
        => At(FieldPosition.Pitcher, name) with { Pitching = PitcherAttributes.LeagueAverage };

    private static Team MakeTeamWithBullpen(params string[] bullpen)
    {
        var t = MakeTeam();
        return t with { Bullpen = bullpen.Select(Rp).ToList() };
    }

    [Fact]
    public void ChangePitcherTo_DesignatesAnyBullpenPitcher_NotOnlyTheHead()
    {
        var state = new TeamState(MakeTeamWithBullpen("第2", "第3", "第4"));
        var third = state.AvailableBullpen[2];

        Assert.True(state.ChangePitcherTo(third));

        Assert.Equal("第4", state.CurrentPitcher.Name);
        Assert.Equal(1, state.PitcherChanges);
        // 指名した投手だけがブルペンから消え、飛ばした投手はまだ登板できる。
        Assert.Equal(new[] { "第2", "第3" }, state.AvailableBullpen.Select(p => p.Name));
        // 非DH制なので打順の投手スロットも入れ替わる。
        Assert.Equal("第4", state.CurrentLineup[8].Name);
    }

    [Fact]
    public void TryChangePitcher_StillTakesTheHeadOfTheBullpen()
    {
        var state = new TeamState(MakeTeamWithBullpen("第2", "第3"));

        Assert.True(state.TryChangePitcher());

        Assert.Equal("第2", state.CurrentPitcher.Name);
        Assert.Equal(new[] { "第3" }, state.AvailableBullpen.Select(p => p.Name));
    }

    [Fact]
    public void ChangePitcherTo_RetiredPitcherCannotReturn()
    {
        var state = new TeamState(MakeTeamWithBullpen("第2"));
        var starter = state.CurrentPitcher;
        var reliever = state.AvailableBullpen[0];

        Assert.True(state.ChangePitcherTo(reliever));

        Assert.True(state.IsRetired(starter));                 // 降板した先発は再登板不可
        Assert.False(state.ChangePitcherTo(starter));           // リエントリー禁止
        Assert.False(state.ChangePitcherTo(state.CurrentPitcher)); // 登板中の投手も指名できない
        Assert.Empty(state.AvailableBullpen);
        Assert.False(state.TryChangePitcher());
    }

    [Fact]
    public void NonDh_PinchHitForPitcherSlot_RetiresPitcher_AndNextChangeRestoresTheSlot()
    {
        // 打順を投手スロット（8番）まで進めてから代打を送る＝非DH制の「投手に代打」。
        var sub = At(FieldPosition.FirstBase, "代打", contact: 90);
        var team = MakeTeamWithBullpen("第2") with { Bench = new[] { sub } };
        var state = new TeamState(team);
        for (var i = 0; i < 8; i++) state.NextBatter();
        var starter = state.CurrentPitcher;
        Assert.Equal(starter, state.PeekBatter());

        Assert.True(state.PinchHitNext(sub));
        Assert.Equal("代打", state.CurrentLineup[8].Name);
        Assert.True(state.IsRetired(starter));   // 投手は退場＝守備再開前に必ず継投が要る（C-2）

        // 継投すると投手スロットの打者が新投手で上書きされ、9人が成立する。
        Assert.True(state.TryChangePitcher());
        Assert.Equal("第2", state.CurrentLineup[8].Name);
        Assert.Equal(9, state.DefensiveAlignment(new FieldGeometry()).Count);
    }

    [Fact]
    public void Dh_ReleaseThenChangePitcher_NewPitcherBatsInTheFormerDhSlot_AndDhNeverReturns()
    {
        var team = MakeDhTeam() with { Bullpen = new[] { Rp("第2") } };
        var state = new TeamState(team);

        Assert.True(state.ReleaseDh());
        Assert.False(state.UsesDh);
        Assert.False(state.ReleaseDh());   // 不可逆＝そのゲーム中DHは復活しない

        Assert.True(state.ChangePitcherTo(state.AvailableBullpen[0]));
        // DH解除後は投手が打順内（元DHスロット）。継投で打者としても入れ替わる。
        Assert.Equal(8, state.PitcherSlot);
        Assert.Equal("第2", state.CurrentLineup[8].Name);
        Assert.Equal(state.CurrentPitcher, state.CurrentLineup[8]);
        Assert.Equal(9, state.DefensiveAlignment(new FieldGeometry()).Count);
    }

    [Fact]
    public void Dh_ChangePitcherWhileDhAlive_DoesNotTouchTheBattingOrder()
    {
        var team = MakeDhTeam() with { Bullpen = new[] { Rp("第2") } };
        var state = new TeamState(team);
        var before = state.CurrentLineup.ToList();

        Assert.True(state.ChangePitcherTo(state.AvailableBullpen[0]));

        Assert.True(state.UsesDh);                  // DH制は継投では消えない
        Assert.Equal(before, state.CurrentLineup);  // 投手は打順外なのでラインナップ不変
        Assert.Equal("第2", state.CurrentPitcher.Name);
    }
}

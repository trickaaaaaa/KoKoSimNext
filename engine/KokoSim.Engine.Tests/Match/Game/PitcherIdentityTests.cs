using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 投手の同定（issue #352）。打順を並べ替えても「マウンドに立つのは『投』の選手」であり続けること、
/// そのうえで<b>必ず実体化を経て本人の能力で投げる</b>こと（LeagueAverage へ落ちない, issue #209 Phase B）を固定する。
/// 回帰の実害: 相手校の調子ベース並べ替え（issue #48）でエースが上位打順へ動いても PitcherSlot=8 が
/// 古いまま残り、9番へ落ちた野手が登板扱い＋リーグ平均能力で投げていた。
/// </summary>
public sealed class PitcherIdentityTests
{
    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher,
    };

    private static Player At(FieldPosition pos, string name, Condition cond = Condition.Normal)
        => new() { Position = pos, Name = name, Condition = cond };

    private static Player Ace(string name = "エース", Condition cond = Condition.Normal, int control = 70)
        => At(FieldPosition.Pitcher, name, cond) with
        { Pitching = PitcherAttributes.LeagueAverage with { Control = control } };

    /// <summary>投手を9番に置いた標準編成（StrengthTeamFactory / AiTeamBuilder の出力と同じ形）。</summary>
    private static Team MakeTeam(IEnumerable<Player>? bullpen = null)
    {
        var order = Positions.Select((p, i) => p == FieldPosition.Pitcher ? Ace() : At(p, $"{p}先発")).ToList();
        return new Team
        {
            Name = "テスト校",
            BattingOrder = order,
            PitcherSlot = 8,
            Bullpen = bullpen?.ToList() ?? new List<Player>(),
        };
    }

    // ===== 実戦の経路の再現（相手校生成 → 当季調子 → 調子ベースの並べ替え） =====

    [Fact]
    public void ConditionReorderedOpponent_MoundIsTheAce_NotTheNinthBatter()
    {
        // 相手校を校ID＋年度から生成（投手は9番・野手は LatentPitching のみ＝実戦と同じ材料）。
        var team = StrengthTeamFactory.ForSchool(
            new School { Id = 3, Name = "大磯総合", PrefectureId = 0, Strength = 70 }, yearIndex: 1);
        var ace = team.BattingOrder[team.PitcherSlot];

        // 当季の調子variance（Q21, 2026-07-22 解決）で並べ替えが非恒等になる状況を作る。
        // 投手が調子ソートで動くのは打撃型エース（野手平均以上）だけになった（issue #360）ので、
        // エースの打撃も引き上げて「投手が9番から動く」再現条件を維持する。
        var withCondition = team.BattingOrder
            .Select((p, i) => i switch
            {
                8 => p with { Condition = Condition.Excellent, Contact = 90, Power = 90 },   // 絶好調の打撃型エースは上位打順へ動く
                3 => p with { Condition = Condition.Terrible },    // 絶不調の野手が9番へ落ちる
                _ => p,
            }).ToList();
        team = team with { BattingOrder = withCondition };
        ace = team.BattingOrder[team.PitcherSlot];

        var reordered = new StandardTacticsBrain().ComposeBattingOrder(team.BattingOrder);
        var composed = team.WithBattingOrder(reordered);

        // 回帰の再現条件: エースは9番から動き、代わりに野手が9番に居る。
        Assert.NotEqual(8, composed.PitcherSlot);
        Assert.NotEqual(FieldPosition.Pitcher, composed.BattingOrder[8].Position);
        Assert.Null(composed.BattingOrder[8].Pitching);            // ＝旧実装でマウンドに立っていた選手

        // 投手枠は「投」の選手を指し続ける。
        Assert.Equal(FieldPosition.Pitcher, composed.BattingOrder[composed.PitcherSlot].Position);
        Assert.Equal(ace.Name, composed.BattingOrder[composed.PitcherSlot].Name);

        var state = new TeamState(composed);
        Assert.Equal(ace.Name, state.CurrentPitcher.Name);
        Assert.Equal(FieldPosition.Pitcher, state.CurrentPitcher.Position);
        Assert.NotNull(state.CurrentPitcher.Pitching);             // リーグ平均へ落ちない
        Assert.Equal(ace.Pitching, state.CurrentPitcher.Pitching);
    }

    // ===== Team.WithBattingOrder（枠の引き直しの単一入口） =====

    [Fact]
    public void WithBattingOrder_PitcherMovedToLeadoff_PitcherSlotFollows()
    {
        var team = MakeTeam();
        var reordered = new[] { team.BattingOrder[8] }.Concat(team.BattingOrder.Take(8)).ToList();

        var composed = team.WithBattingOrder(reordered);

        Assert.Equal(0, composed.PitcherSlot);
        Assert.Equal(FieldPosition.Pitcher, composed.BattingOrder[composed.PitcherSlot].Position);
        Assert.Equal(-1, composed.DhSlot);   // 非DHは触らない
    }

    [Fact]
    public void WithBattingOrder_Dh_DhSlotFollows_AndPitcherSlotUntouched()
    {
        // DH制: 投手は打順外（StartingPitcher）＝PitcherSlot は打順を指していないので引き直さない。
        var order = Positions.Take(8).Select((p, i) => At(p, $"{p}先発"))
            .Append(At(FieldPosition.DesignatedHitter, "DH")).ToList();
        var team = new Team
        {
            Name = "DH校", BattingOrder = order, DhSlot = 8, PitcherSlot = -1, StartingPitcher = Ace(),
        };
        var reordered = new[] { order[8] }.Concat(order.Take(8)).ToList();

        var composed = team.WithBattingOrder(reordered);

        Assert.Equal(0, composed.DhSlot);
        Assert.Equal(-1, composed.PitcherSlot);
        Assert.Equal("DH", composed.BattingOrder[composed.DhSlot].Name);
        Assert.Equal("エース", new TeamState(composed).CurrentPitcher.Name);
    }

    [Fact]
    public void WithBattingOrder_NotAPermutation_Throws()
    {
        var team = MakeTeam();
        // 別インスタンス（同値のコピー）で差し替え＝順列ではない。黙って -1 にせず落とす。
        var copies = team.BattingOrder.Select(p => p with { }).ToList();

        Assert.Throws<System.ArgumentException>(() => team.WithBattingOrder(copies));
        Assert.Throws<System.ArgumentException>(() => team.WithBattingOrder(team.BattingOrder.Take(8).ToList()));
    }

    // ===== 実体化（#209 Phase B）: マウンドに立つ選手は必ず本人の能力で投げる =====

    [Fact]
    public void FielderAtPitcherSlot_MaterializesLatentPitching_NotLeagueAverage()
    {
        // 「投」枠に野手を置く編成自体は正（issue #93: 内部の投/野区分でゲートしない）。
        // 禁じるのは実体化を経ずに PitchingFatigue の LeagueAverage へ落ちること。
        var latent = PitcherAttributes.LeagueAverage with { Control = 33, MaxVelocityKmh = 121 };
        var order = Positions.Select((p, i) => p == FieldPosition.Pitcher
            ? At(FieldPosition.CenterField, "野手登板") with { LatentPitching = latent }
            : At(p, $"{p}先発")).ToList();
        var team = new Team { Name = "野手登板校", BattingOrder = order, PitcherSlot = 8 };

        var state = new TeamState(team);

        Assert.Equal("野手登板", state.CurrentPitcher.Name);
        Assert.Equal(latent, state.CurrentPitcher.Pitching);
        // 打順側も実体化後の同一インスタンスに揃う（成績の帰属キーが割れない）。
        Assert.Same(state.CurrentPitcher, state.CurrentLineup[state.PitcherSlot]);
    }

    [Fact]
    public void ProfessionalPitcherAtSlot_MaterializeIsIdentity_LineupInstanceUnchanged()
    {
        var team = MakeTeam();

        var state = new TeamState(team);

        Assert.Same(team.BattingOrder[8], state.CurrentPitcher);              // 恒等＝帯・決定論不変
        Assert.Same(team.BattingOrder[8], state.CurrentLineup[8]);
    }

    // ===== 継投: 打順内に「投」が二人残らない =====

    [Fact]
    public void Relief_AfterReorder_ReplacesTheAceSlot_LeavesExactlyOnePitcherInLineup()
    {
        var relief = Ace("リリーフ", control: 55);
        var team = MakeTeam(new[] { relief });
        // エースを4番へ動かす（実戦の調子並べ替えと同じ形）。
        var reordered = team.BattingOrder.Take(3)
            .Append(team.BattingOrder[8])
            .Concat(team.BattingOrder.Skip(3).Take(5)).ToList();
        var composed = team.WithBattingOrder(reordered);
        Assert.Equal(3, composed.PitcherSlot);

        var state = new TeamState(composed);
        Assert.True(state.TryChangePitcher());

        Assert.Equal("リリーフ", state.CurrentPitcher.Name);
        Assert.Same(state.CurrentPitcher, state.CurrentLineup[state.PitcherSlot]);
        // 交代先が古い9番でなく実際の投手枠＝打順に「投」は一人だけ（旧投手が打順に残らない）。
        Assert.Equal(1, state.CurrentLineup.Count(p => p.Position == FieldPosition.Pitcher));
        Assert.DoesNotContain(state.CurrentLineup, p => p.Name == "エース");
    }
}

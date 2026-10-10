using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 守備位置を書き換えた後（コンバート・投野交代・DH解除）に、<b>書き換える前の実体</b>で交代APIを
/// 呼んでも成立すること。<see cref="Player"/> は record ＝値等価に <see cref="Player.Position"/> を含むので、
/// TeamState の <c>with { Position = ... }</c> を挟むと同じ選手の実体が別物になる。
/// 従来は <c>IndexOf</c> がそれを取りこぼして -1 を返し、代走・守備固めが<b>例外も記録も残さず
/// 黙って不成立</b>になっていた（登板指名も控え扱いへ落ちて false）。
/// </summary>
public sealed class SubstitutionIdentityTests
{
    private static Player At(FieldPosition pos, string name)
        => new() { Position = pos, Name = name, UniformNumber = (int)pos + 1 };

    private static Player Rp(string name, int number)
        => new() { Position = FieldPosition.Pitcher, Name = name, UniformNumber = number,
                   Pitching = PitcherAttributes.LeagueAverage };

    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher,
    };

    private const int LeftSlot = 5;
    private const int CenterSlot = 6;

    /// <summary>控えは「守備が下手な控え野手」1枚＝在フィールド野手の登板が①（投野交代）に倒れる編成。</summary>
    private static Team MakeTeam(bool withBench = true)
    {
        var order = Positions.Select(p => p == FieldPosition.Pitcher ? Rp("先発P", 1) : At(p, $"{p}先発")).ToList();
        var bench = withBench
            ? new List<Player> { new() { Position = FieldPosition.CenterField, Name = "控え野手", UniformNumber = 12, Fielding = 20 } }
            : new List<Player>();
        return new Team
        {
            Name = "テスト校", BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(new List<Player>(), bench),
        };
    }

    [Fact]
    public void コンバート前の実体でも守備固めが成立する()
    {
        var state = new TeamState(MakeTeam());
        var stale = state.CurrentLineup[LeftSlot];              // コンバート前の実体
        Assert.True(state.SwapDefensivePositions(LeftSlot, CenterSlot));
        var sub = state.AvailableReserves[0];

        Assert.True(state.DefensiveSub(stale, sub));

        Assert.Equal(sub.Name, state.CurrentLineup[LeftSlot].Name);
        // 退いた選手はコンバート前の実体で問い合わせても退場済み（交代UIのグレーアウト判定）。
        Assert.True(state.IsRetired(stale));
    }

    [Fact]
    public void コンバート前の実体でも代走が成立し塁へ差す実体が打順と一致する()
    {
        var state = new TeamState(MakeTeam());
        var stale = state.CurrentLineup[LeftSlot];
        Assert.True(state.SwapDefensivePositions(LeftSlot, CenterSlot));
        var sub = state.AvailableReserves[0];

        Assert.True(state.PinchRunFor(stale, sub, out var inserted));

        // 塁へ差す実体は打順の実体そのもの（組み直すとズレて得点の帰属先が割れる）。
        Assert.Same(state.CurrentLineup[LeftSlot], inserted);
        Assert.Equal(FieldPosition.CenterField, inserted!.Position);   // コンバート後の守備位置を継承
    }

    [Fact]
    public void コンバート前の実体でも在フィールド野手の登板が成立し空けるのは現在の守備位置()
    {
        var state = new TeamState(MakeTeam(withBench: false));
        var stale = state.CurrentLineup[LeftSlot];
        var starter = state.CurrentPitcher;
        Assert.True(state.SwapDefensivePositions(LeftSlot, CenterSlot));

        Assert.True(state.RelieveWith(stale, allowBenchBackfill: false));

        Assert.Equal(stale.Name, state.CurrentPitcher.Name);
        // 空くのはコンバート「後」の中堅＝旧投手はそこへ回る（古い実体の左翼を空けたことにしない）。
        Assert.Equal(starter.Name, state.PlayerAtPosition(FieldPosition.CenterField)?.Name);
        Assert.Equal($"{FieldPosition.CenterField}先発", state.PlayerAtPosition(FieldPosition.LeftField)?.Name);
    }

    /// <summary>守備位置が動いていない試合では従来と同じ（読み替え表が空＝素の値一致）。</summary>
    [Fact]
    public void 守備位置が動いていなければ従来どおり解決する()
    {
        var state = new TeamState(MakeTeam());
        var lf = state.CurrentLineup[LeftSlot];
        var sub = state.AvailableReserves[0];

        Assert.True(state.DefensiveSub(lf, sub));
        Assert.Equal(sub.Name, state.CurrentLineup[LeftSlot].Name);
        Assert.True(state.IsRetired(lf));
        Assert.False(state.IsRetired(state.CurrentLineup[CenterSlot]));
    }
}

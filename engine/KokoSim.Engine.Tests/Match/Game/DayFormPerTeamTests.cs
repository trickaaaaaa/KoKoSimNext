using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue dayform-dictionary-shared-across-teams: 当日の出来（dayForm）はチームごとに別辞書へ持たせる。
/// <see cref="Player"/> は値等価の record なので、両チームを1本の辞書へ merge すると、全属性が偶然一致する
/// 別校の選手が同一キーに潰れ、片方の当日の出来がもう片方の値で上書きされる（先攻→後攻の順で抽選するため、
/// 修正前は後攻の値が勝つ）。同じ理由で既に分けている <see cref="TeamState.SetPitchRecovery"/> と同型の修正。
/// </summary>
public sealed class DayFormPerTeamTests
{
    // 全属性が一致するプレイヤーを両チームへ同じ打順位置で配置する（衝突ワーストケース）。
    private static Player Pos(FieldPosition pos) => new() { Position = pos, Contact = 50, Power = 50 };

    private static Team TeamOf(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            new() { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage },
        };
        // 名前もチーム名も含めない＝両チームの選手が完全に値等価（Player record の全プロパティ一致）になる。
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    [Fact]
    public void SampleDayForms_IdenticalPlayersAcrossTeams_DoNotOverwriteEachOther()
    {
        var away = TeamOf("A");
        var home = TeamOf("H");
        // 打順の各スロットで away/home の Player は完全に値等価（Equals true）＝辞書1本なら衝突する。
        for (var i = 0; i < away.BattingOrder.Count; i++)
            Assert.Equal(away.BattingOrder[i], home.BattingOrder[i]);

        var ctx = new GameContext();
        var rng = new Xoshiro256Random(1);
        var progress = GameEngine.NewProgress(away, home, ctx, rng);

        // 連続値のガウス/スパイク分布なので、独立抽選なら一致確率は実質ゼロ。
        // 修正前は1本の辞書に後攻(home)が上書きするため、away 側が home の値と完全一致してしまっていた。
        var mismatches = away.BattingOrder
            .Select((p, i) => (away: progress.Away.DayFormOf(p), home: progress.Home.DayFormOf(home.BattingOrder[i])))
            .Count(t => t.away != t.home);
        Assert.True(mismatches > 0,
            "全打順でaway/homeのdayFormが一致した＝辞書が共有され上書きされている疑い");
    }
}

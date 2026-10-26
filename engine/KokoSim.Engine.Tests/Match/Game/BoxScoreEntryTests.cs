using System.Linq;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// ボックススコアの途中出場表記（issue #680）。代打・代走・守備交代で入った選手を、入った打順枠の直下に
/// 「どう入ったか・誰の代わりか・実際に守った位置」つきで載せる。選手本来の守備適性（Player.Position）は
/// 表示に使わない＝守備に就かなかった代打・代走の FieldedAt は null。
/// </summary>
public sealed class BoxScoreEntryTests
{
    private static Player At(FieldPosition pos, string name, int number)
        => new() { Position = pos, Name = name, UniformNumber = number };

    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher,
    };

    private const int LeftSlot = 5;

    private static Team MakeTeam()
    {
        var order = Positions.Select((p, i) => p == FieldPosition.Pitcher
            ? new Player { Position = p, Name = "先発P", UniformNumber = 1, Pitching = PitcherAttributes.LeagueAverage }
            : At(p, $"{p}先発", i + 2)).ToList();
        return new Team
        {
            Name = "テスト校", BattingOrder = order, PitcherSlot = 8,
            // 本来の守備適性は「中堅」。守備に就くまでは表示に出ないことを確かめる。
            Reserves = new[] { At(FieldPosition.CenterField, "控え中", 12), At(FieldPosition.FirstBase, "控え一", 13) },
        };
    }

    private static void DefendOnePa(TeamState s)
        => s.RecordPitching(s.CurrentPitcher, PlateAppearanceResult.InPlayOut, runs: 0, outs: 1, pitches: 3);

    [Fact]
    public void 代打は打順枠の直下に出て_守備に就くまで守備位置を持たない()
    {
        var s = new TeamState(MakeTeam());
        var starter = s.CurrentLineup[LeftSlot];
        var pinch = s.Reserves.First(r => r.Name == "控え中");
        Assert.True(s.SubstituteForTest(LeftSlot, pinch));

        var lines = s.BuildBattingLines();
        var i = lines.ToList().FindIndex(l => l.Name == "控え中");
        Assert.Equal(LeftSlot + 1, lines[i].Order);
        Assert.Equal(starter.Name, lines[i - 1].Name);          // 代わった選手の直下
        Assert.Equal(BoxEntry.PinchHit, lines[i].Entry);
        Assert.Equal(starter.Name, lines[i].EnteredFor);
        Assert.Null(lines[i].FieldedAt);                        // 本来の適性（中堅）は出さない
        Assert.Equal(BoxEntry.Starter, lines[i - 1].Entry);
    }

    [Fact]
    public void 代打のあと守備についたら_就いた位置が入る()
    {
        var s = new TeamState(MakeTeam());
        Assert.True(s.SubstituteForTest(LeftSlot, s.Reserves.First(r => r.Name == "控え中")));
        DefendOnePa(s);

        var line = s.BuildBattingLines().Single(l => l.Name == "控え中");
        Assert.Equal(FieldPosition.LeftField, line.FieldedAt);  // 継承した左翼であって、本来の中堅ではない
    }

    [Fact]
    public void 代走は打席も得点も無くても行が出る()
    {
        var s = new TeamState(MakeTeam());
        var runner = s.CurrentLineup[2];
        Assert.True(s.PinchRunFor(runner, s.Reserves.First(r => r.Name == "控え一")));

        var line = s.BuildBattingLines().Single(l => l.Name == "控え一");
        Assert.Equal(BoxEntry.PinchRun, line.Entry);
        Assert.Equal(runner.Name, line.EnteredFor);
        Assert.Equal(3, line.Order);
        Assert.Equal(0, line.PlateAppearances);
        Assert.Null(line.FieldedAt);
    }

    [Fact]
    public void 守備交代は入った枠と守った位置が出る()
    {
        var s = new TeamState(MakeTeam());
        var outgoing = s.CurrentLineup[LeftSlot];
        Assert.True(s.DefensiveSub(outgoing, s.Reserves.First(r => r.Name == "控え一")));
        DefendOnePa(s);

        var line = s.BuildBattingLines().Single(l => l.Name == "控え一");
        Assert.Equal(BoxEntry.DefensiveSub, line.Entry);
        Assert.Equal(LeftSlot + 1, line.Order);
        Assert.Equal(FieldPosition.LeftField, line.FieldedAt);
    }
}

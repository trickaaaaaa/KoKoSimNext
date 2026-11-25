using System.Collections.Generic;
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
/// 表示に使わない＝守備に就かなかった代打・代走の Positions は空。
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
        Assert.Empty(lines[i].Positions);                       // 本来の適性（中堅）は出さない
        Assert.Equal(BoxEntry.Starter, lines[i - 1].Entry);
    }

    [Fact]
    public void 代打のあと守備についたら_就いた位置が入る()
    {
        var s = new TeamState(MakeTeam());
        Assert.True(s.SubstituteForTest(LeftSlot, s.Reserves.First(r => r.Name == "控え中")));
        DefendOnePa(s);

        var line = s.BuildBattingLines().Single(l => l.Name == "控え中");
        Assert.Equal(new[] { FieldPosition.LeftField }, line.Positions);  // 継承した左翼であって、本来の中堅ではない
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
        Assert.Empty(line.Positions);
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
        Assert.Equal(new[] { FieldPosition.LeftField }, line.Positions);
    }

    // ===== 守った位置の並び（issue #1608） =====

    private static IReadOnlyList<FieldPosition> TrailOf(TeamState s, string name)
        => s.BuildBattingLines().Single(l => l.Name == name).Positions;

    private static Team MakeDhTeam()
    {
        var team = MakeTeam();
        var order = team.BattingOrder.ToList();
        // 投手枠（8）を DH 枠に置き換え、投手は打順外から。
        order[8] = At(FieldPosition.DesignatedHitter, "DH先発", 20);
        return new Team
        {
            Name = "DH校", BattingOrder = order, PitcherSlot = 8, DhSlot = 8,
            StartingPitcher = new Player { Position = FieldPosition.Pitcher, Name = "先発P", UniformNumber = 1, Pitching = PitcherAttributes.LeagueAverage },
            Reserves = team.Reserves,
        };
    }

    [Fact]
    public void 位置替えなしのスタメンは_開始時の位置1つだけ()
    {
        var s = new TeamState(MakeTeam());
        DefendOnePa(s);
        DefendOnePa(s);

        Assert.Equal(new[] { FieldPosition.LeftField }, TrailOf(s, "LeftField先発"));
        Assert.Equal(new[] { FieldPosition.Pitcher }, TrailOf(s, "先発P"));
    }

    [Fact]
    public void スタメンが途中で位置を替えたら_並びに続く()
    {
        var s = new TeamState(MakeTeam());
        DefendOnePa(s);
        Assert.True(s.SwapDefensivePositions(4, 5));   // 遊撃 ⇔ 左翼
        DefendOnePa(s);

        Assert.Equal(new[] { FieldPosition.Shortstop, FieldPosition.LeftField }, TrailOf(s, "Shortstop先発"));
        Assert.Equal(new[] { FieldPosition.LeftField, FieldPosition.Shortstop }, TrailOf(s, "LeftField先発"));
        Assert.Equal(new[] { FieldPosition.Catcher }, TrailOf(s, "Catcher先発"));
    }

    [Fact]
    public void 同じ選手が2回位置を替えたら_3つ並ぶ_元の位置へ戻れば4つ目も足す()
    {
        var s = new TeamState(MakeTeam());
        DefendOnePa(s);
        Assert.True(s.SwapDefensivePositions(4, 5));   // 遊 → 左
        DefendOnePa(s);
        Assert.True(s.SwapDefensivePositions(4, 6));   // 左翼にいた先発(遊)は slot4: 左 → 中
        DefendOnePa(s);
        Assert.Equal(new[] { FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField },
            TrailOf(s, "Shortstop先発"));
    }

    [Fact]
    public void 位置替えが打席の合間に戻っていたら_足さない()
    {
        var s = new TeamState(MakeTeam());
        DefendOnePa(s);
        Assert.True(s.SwapDefensivePositions(4, 5));
        Assert.True(s.SwapDefensivePositions(4, 5));   // 打席を挟まず元へ
        DefendOnePa(s);

        Assert.Equal(new[] { FieldPosition.Shortstop }, TrailOf(s, "Shortstop先発"));
    }

    [Fact]
    public void 守備固めは_就いた位置から始まり_そのまま位置替えも続く()
    {
        var s = new TeamState(MakeTeam());
        Assert.True(s.DefensiveSub(s.CurrentLineup[LeftSlot], s.Reserves.First(r => r.Name == "控え一")));
        // 打席が1つも無くても、入った位置から始まる。
        Assert.Equal(new[] { FieldPosition.LeftField }, TrailOf(s, "控え一"));

        DefendOnePa(s);
        Assert.True(s.SwapDefensivePositions(LeftSlot, 6));   // 左 ⇔ 中
        DefendOnePa(s);
        Assert.Equal(new[] { FieldPosition.LeftField, FieldPosition.CenterField }, TrailOf(s, "控え一"));
    }

    [Fact]
    public void 代打だけなら空_代打のあと位置を替えたら並びになる()
    {
        var s = new TeamState(MakeTeam());
        Assert.True(s.SubstituteForTest(LeftSlot, s.Reserves.First(r => r.Name == "控え中")));
        Assert.Empty(TrailOf(s, "控え中"));
        Assert.Equal(BoxEntry.PinchHit, s.BuildBattingLines().Single(l => l.Name == "控え中").Entry);

        DefendOnePa(s);
        Assert.True(s.SwapDefensivePositions(LeftSlot, 6));
        DefendOnePa(s);
        Assert.Equal(new[] { FieldPosition.LeftField, FieldPosition.CenterField }, TrailOf(s, "控え中"));
    }

    [Fact]
    public void 代走のあと守備についたら_就いた位置が並ぶ()
    {
        var s = new TeamState(MakeTeam());
        Assert.True(s.PinchRunFor(s.CurrentLineup[LeftSlot], s.Reserves.First(r => r.Name == "控え一")));
        Assert.Empty(TrailOf(s, "控え一"));

        DefendOnePa(s);
        Assert.Equal(new[] { FieldPosition.LeftField }, TrailOf(s, "控え一"));
        Assert.Equal(BoxEntry.PinchRun, s.BuildBattingLines().Single(l => l.Name == "控え一").Entry);
    }

    [Fact]
    public void DHは指から始まり_DH解除で守備についたらその位置が続く()
    {
        var s = new TeamState(MakeDhTeam());
        DefendOnePa(s);
        Assert.Equal(new[] { FieldPosition.DesignatedHitter }, TrailOf(s, "DH先発"));

        Assert.True(s.ReleaseDh(FieldPosition.LeftField));   // DH が左翼へ
        DefendOnePa(s);
        Assert.Equal(new[] { FieldPosition.DesignatedHitter, FieldPosition.LeftField }, TrailOf(s, "DH先発"));
    }

    [Fact]
    public void DH解除で守備に就かず退いたDHは_指のまま()
    {
        var s = new TeamState(MakeDhTeam());
        DefendOnePa(s);
        Assert.True(s.ReleaseDh());
        DefendOnePa(s);
        Assert.Equal(new[] { FieldPosition.DesignatedHitter }, TrailOf(s, "DH先発"));
    }

    [Fact]
    public void 位置の並びは試合結果の指紋に触れない_観測だけ()
    {
        // 同じシードの試合を2回回しても並びを含めて同じ（乱数を使わない）。
        static GameResult Play()
        {
            var rng = new KokoSim.Engine.Core.Xoshiro256Random(7);
            var away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(52, "A", rng);
            var home = KokoSim.Engine.Nation.StrengthTeamFactory.Create(52, "H", rng);
            return GameEngine.Play(away, home, new GameContext(), rng);
        }
        var a = Play();
        var b = Play();
        Assert.Equal(a.AwayBatting.Select(l => string.Join(",", l.Positions)), b.AwayBatting.Select(l => string.Join(",", l.Positions)));
        Assert.All(a.AwayBatting.Concat(a.HomeBatting).Where(l => l.Entry == BoxEntry.Starter), l => Assert.NotEmpty(l.Positions));
    }
}

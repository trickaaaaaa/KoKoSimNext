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

    // ===== DH制の先発投手が途中から打順に入る（issue #1726） =====

    private const int SecondSlot = 2;
    private const int DhSlot = 8;

    [Fact]
    public void DH解除で投手が打順に入ったら_打席が無くても入った枠の直下に出る()
    {
        var s = new TeamState(MakeDhTeam());
        Assert.True(s.ReleaseDh(FieldPosition.SecondBase));
        DefendOnePa(s);

        var lines = s.BuildBattingLines().ToList();
        var i = lines.FindIndex(l => l.Name == "先発P");
        Assert.True(i > 0);
        Assert.Equal("SecondBase先発", lines[i - 1].Name);   // 退いた二塁手の直下
        Assert.Equal(SecondSlot + 1, lines[i].Order);
        Assert.Equal("SecondBase先発", lines[i].EnteredFor);
        Assert.Equal(BoxEntry.Starter, lines[i].Entry);
        Assert.Equal(0, lines[i].PlateAppearances);
        Assert.Equal(new[] { FieldPosition.Pitcher }, lines[i].Positions);
    }

    [Fact]
    public void DH解除で守備に就かせない形でも_投手はDH枠の直下に出る()
    {
        var s = new TeamState(MakeDhTeam());
        Assert.True(s.ReleaseDh());
        DefendOnePa(s);

        var lines = s.BuildBattingLines().ToList();
        var i = lines.FindIndex(l => l.Name == "先発P");
        Assert.Equal("DH先発", lines[i - 1].Name);
        Assert.Equal(DhSlot + 1, lines[i].Order);
    }

    [Fact]
    public void DH解除のあと元DHを登板させたら_先発投手は投から二の並びで入った枠に出る()
    {
        var s = new TeamState(MakeDhTeam());
        Assert.True(s.ReleaseDh(FieldPosition.SecondBase));
        DefendOnePa(s);
        // 元DH（二塁へ入った選手）を投野交代でマウンドへ。先発投手は二塁へ回る。
        Assert.True(s.RelieveWith(s.CurrentLineup[DhSlot], allowBenchBackfill: false));
        DefendOnePa(s);

        var lines = s.BuildBattingLines().ToList();
        var i = lines.FindIndex(l => l.Name == "先発P");
        Assert.Equal("SecondBase先発", lines[i - 1].Name);
        Assert.Equal(SecondSlot + 1, lines[i].Order);
        Assert.Equal(new[] { FieldPosition.Pitcher, FieldPosition.SecondBase }, lines[i].Positions);
    }

    [Fact]
    public void DH解除で入った投手に打席があっても_入った枠の直下に出る()
    {
        var s = new TeamState(MakeDhTeam());
        Assert.True(s.ReleaseDh(FieldPosition.SecondBase));
        s.RecordBatting(s.CurrentLineup[SecondSlot], PlateAppearanceResult.Single, rbi: 0);

        var lines = s.BuildBattingLines().ToList();
        var i = lines.FindIndex(l => l.Name == "先発P");
        Assert.Equal("SecondBase先発", lines[i - 1].Name);
        Assert.Equal(SecondSlot + 1, lines[i].Order);
        Assert.Equal(1, lines[i].AtBats);
        Assert.Equal(1, lines[i].Hits);
        Assert.Single(lines, l => l.Name == "先発P");
    }

    [Fact]
    public void 救援投手が打席に立った行も_入った枠の直下に出る()
    {
        // 投手枠が継投で動いた後（投野交代）に控え投手が上がって打つ。元の投手枠（9番）に決め打ちしない。
        var s = new TeamState(MakeTeam());
        Assert.True(s.RelieveWith(s.CurrentLineup[LeftSlot], allowBenchBackfill: false));   // 左翼手が登板・投手は左翼へ
        var reliever = s.Reserves.First(r => r.Name == "控え一");
        Assert.True(s.RelieveWith(reliever));
        s.RecordBatting(s.CurrentLineup[LeftSlot], PlateAppearanceResult.Single, rbi: 0);

        var line = s.BuildBattingLines().Single(l => l.Name == "控え一");
        Assert.Equal(LeftSlot + 1, line.Order);
    }

    [Fact]
    public void コンバート継投のDH制の枝でも_元投手は押し出された野手の枠の直下に出る()
    {
        var s = new TeamState(MakeDhTeam());
        Assert.True(s.ConvertPitcherAndRelieve(FieldPosition.LeftField, s.Reserves.First(r => r.Name == "控え一")));
        DefendOnePa(s);

        var lines = s.BuildBattingLines().ToList();
        var i = lines.FindIndex(l => l.Name == "先発P");
        Assert.Equal("LeftField先発", lines[i - 1].Name);
        Assert.Equal(LeftSlot + 1, lines[i].Order);
        Assert.Equal(new[] { FieldPosition.Pitcher, FieldPosition.LeftField }, lines[i].Positions);
    }

    private static Team WithStrongBenchDefender(Team team, bool dh)
    {
        var order = team.BattingOrder.ToList();
        if (dh) order[DhSlot] = order[DhSlot] with { Contact = 1, Power = 1, Fielding = 1 };
        var reserves = team.Reserves.Select(r => r.Name == "控え中" ? r with { Fielding = 99 } : r).ToList();
        return new Team
        {
            Name = team.Name, BattingOrder = order, PitcherSlot = 8, DhSlot = dh ? DhSlot : -1,
            StartingPitcher = team.StartingPitcher, Reserves = reserves,
        };
    }

    [Fact]
    public void 野手登板でDHが消える枝_ベンチ野手の補充も打席が無くても出る()
    {
        var s = new TeamState(WithStrongBenchDefender(MakeDhTeam(), dh: true));
        Assert.True(s.RelieveWith(s.CurrentLineup[LeftSlot]));
        Assert.Equal("控え中", s.CurrentLineup[DhSlot].Name);   // ②でベンチ野手が入った
        DefendOnePa(s);

        var lines = s.BuildBattingLines().ToList();
        var i = lines.FindIndex(l => l.Name == "控え中");
        Assert.Equal("DH先発", lines[i - 1].Name);
        Assert.Equal(DhSlot + 1, lines[i].Order);
        Assert.Equal(BoxEntry.DefensiveSub, lines[i].Entry);
        Assert.Equal(new[] { FieldPosition.LeftField }, lines[i].Positions);
    }

    [Fact]
    public void 非DHの継投で控え野手が空き守備に入る形も_打席が無くても出る()
    {
        var s = new TeamState(WithStrongBenchDefender(MakeTeam(), dh: false));
        Assert.True(s.RelieveWith(s.CurrentLineup[LeftSlot]));
        Assert.Equal("控え中", s.CurrentLineup[8].Name);
        DefendOnePa(s);

        var lines = s.BuildBattingLines().ToList();
        var i = lines.FindIndex(l => l.Name == "控え中");
        Assert.Equal("先発P", lines[i - 1].Name);
        Assert.Equal(9, lines[i].Order);
        Assert.Equal(new[] { FieldPosition.LeftField }, lines[i].Positions);
    }
}

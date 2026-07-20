using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

public sealed class GameEngineTests
{
    private static Player Runner(int speed = 50) => new() { Speed = speed };
    private static readonly BaserunningCoefficients BR = new();

    // --- 走塁モデル ---

    [Fact]
    public void HomeRun_WithBasesLoaded_ScoresFour_AndClearsBases()
    {
        var bases = new BaseState { First = Runner(), Second = Runner(), Third = Runner() };
        var (runs, extra) = BaserunningModel.Apply(
            bases, PlateAppearanceResult.HomeRun, Runner(), 0, BR, new Xoshiro256Random(1));
        Assert.Equal(4, runs);
        Assert.Equal(0, extra);
        Assert.Equal(0, bases.RunnerCount);
    }

    [Fact]
    public void Triple_ScoresAllRunners_AndBatterToThird()
    {
        var bases = new BaseState { First = Runner(), Second = Runner() };
        var (runs, _) = BaserunningModel.Apply(
            bases, PlateAppearanceResult.Triple, Runner(), 0, BR, new Xoshiro256Random(1));
        Assert.Equal(2, runs);
        Assert.NotNull(bases.Third);
        Assert.Null(bases.First);
        Assert.Null(bases.Second);
    }

    [Fact]
    public void Walk_BasesLoaded_ForcesInOneRun()
    {
        var bases = new BaseState { First = Runner(), Second = Runner(), Third = Runner() };
        var (runs, _) = BaserunningModel.Apply(
            bases, PlateAppearanceResult.Walk, Runner(), 1, BR, new Xoshiro256Random(1));
        Assert.Equal(1, runs);
        Assert.Equal(3, bases.RunnerCount); // 依然として満塁
    }

    [Fact]
    public void Walk_RunnerOnFirstOnly_NoRun()
    {
        var bases = new BaseState { First = Runner() };
        var (runs, _) = BaserunningModel.Apply(
            bases, PlateAppearanceResult.Walk, Runner(), 0, BR, new Xoshiro256Random(1));
        Assert.Equal(0, runs);
        Assert.NotNull(bases.First);
        Assert.NotNull(bases.Second);
    }

    [Fact]
    public void InPlayOut_WithTwoOuts_NoAdvance()
    {
        var bases = new BaseState { Third = Runner() };
        var (runs, extra) = BaserunningModel.Apply(
            bases, PlateAppearanceResult.InPlayOut, Runner(), 2, BR, new Xoshiro256Random(1));
        Assert.Equal(0, runs);
        Assert.Equal(0, extra);
        Assert.NotNull(bases.Third);
    }

    // --- チーム状態 ---

    [Fact]
    public void TeamState_BattingOrderRotatesAndWraps()
    {
        var team = BuildTeam("T");
        var state = new TeamState(team);
        var first9 = new List<Player>();
        for (var i = 0; i < 9; i++) first9.Add(state.NextBatter());
        Assert.Equal(first9[0], state.NextBatter()); // 10人目=1番に戻る
    }

    [Fact]
    public void TeamState_PitcherChangeSwapsAndResetsCount()
    {
        var team = BuildTeam("T");
        var state = new TeamState(team);
        var starter = state.CurrentPitcher;
        state.AddPitches(100);
        Assert.True(state.TryChangePitcher());
        Assert.NotEqual(starter, state.CurrentPitcher);
        Assert.Equal(0, state.PitchesThrown);
        Assert.Equal(1, state.PitcherChanges);
    }

    // --- 試合エンジン ---

    [Fact]
    public void Game_ProducesValidResult()
    {
        var ctx = new GameContext();
        for (ulong seed = 0; seed < 30; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), ctx, new Xoshiro256Random(seed));
            Assert.True(r.InningsPlayed >= ctx.RegulationInnings);
            Assert.True(r.AwayRuns >= 0 && r.HomeRuns >= 0);
            Assert.True(r.TotalPitches > 0);
            // 規定回内で決着 or 延長上限での引き分けのみ。
            if (r.InningsPlayed < ctx.MaxInnings) Assert.False(r.Tied);
        }
    }

    [Fact]
    public void Game_IsDeterministic()
    {
        var ctx = new GameContext();
        var a = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), ctx, new Xoshiro256Random(555));
        var b = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), ctx, new Xoshiro256Random(555));
        Assert.Equal(a.AwayRuns, b.AwayRuns);
        Assert.Equal(a.HomeRuns, b.HomeRuns);
        Assert.Equal(a.InningsPlayed, b.InningsPlayed);
        Assert.Equal(a.TotalPitches, b.TotalPitches);
    }

    [Fact]
    public void HomeLeadingAfterTop9_DoesNotBatBottom()
    {
        // 後攻が最終回終了時に勝っていれば InningsPlayed=9 で終わる（裏の攻撃なし or サヨナラ）。
        // 対称チームで多数回し、9回で終わる試合が存在することを確認。
        var ctx = new GameContext();
        var nineInningGames = 0;
        var root = new Xoshiro256Random(3);
        for (var i = 0; i < 40; i++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), ctx, root.Fork((ulong)i));
            if (r.InningsPlayed == 9) nineInningGames++;
        }
        Assert.True(nineInningGames > 0);
    }

    private static Player Position(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Pitcher(string name) => Position(FieldPosition.Pitcher) with
    {
        Name = name,
        Pitching = PitcherAttributes.LeagueAverage,
    };

    private static Team BuildTeam(string name)
    {
        var order = new List<Player>
        {
            Position(FieldPosition.Catcher),
            Position(FieldPosition.FirstBase),
            Position(FieldPosition.SecondBase),
            Position(FieldPosition.ThirdBase),
            Position(FieldPosition.Shortstop),
            Position(FieldPosition.LeftField),
            Position(FieldPosition.CenterField),
            Position(FieldPosition.RightField),
            Pitcher(name + "P"),
        };
        return new Team
        {
            Name = name,
            BattingOrder = order,
            PitcherSlot = 8,
            Bullpen = new[] { Pitcher(name + "R1"), Pitcher(name + "R2") },
        };
    }
}

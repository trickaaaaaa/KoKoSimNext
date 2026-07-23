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

    // --- コールドゲーム（マーシールール, 設計書05 §1.3, OPEN-QUESTIONS Q18） ---

    [Theory]
    [InlineData(5, 0, 9, false)]    // 5回9点差はまだ継続
    [InlineData(5, 0, 10, true)]   // 5回10点差で成立
    [InlineData(4, 0, 15, false)]  // 4回はどれだけ点差があっても未成立（5回未満）
    [InlineData(6, 0, 6, false)]   // 7回未満は7点差では未成立
    [InlineData(7, 0, 7, true)]    // 7回7点差で成立
    [InlineData(7, 0, 6, false)]   // 7回6点差はまだ継続
    public void IsMercy_BoundaryConditions(int inning, int away, int home, bool expected)
        => Assert.Equal(expected, GameEngine.IsMercy(inning, away, home));

    [Fact]
    public void MercyRuleEnabled_EndsGameEarly_WhenLopsided()
    {
        // 弱小チーム相手なら早々に大差がつき、有効時は規定回(9)前に打ち切られるはずの試合を探す。
        var ctx = new GameContext { MercyRuleEnabled = true };
        var strong = BuildTeam("強豪");
        var weak = BuildWeakTeam("弱小");
        var foundMercyEnded = false;
        for (ulong seed = 0; seed < 50; seed++)
        {
            var r = GameEngine.Play(strong, weak, ctx, new Xoshiro256Random(seed));
            if (!r.MercyEnded) continue;
            foundMercyEnded = true;
            Assert.True(r.InningsPlayed < ctx.RegulationInnings || GameEngine.IsMercy(r.InningsPlayed, r.AwayRuns, r.HomeRuns));
            Assert.True(GameEngine.IsMercy(r.InningsPlayed, r.AwayRuns, r.HomeRuns));
        }
        Assert.True(foundMercyEnded, "大差の対戦カードなのにコールドが一度も成立しなかった（設定または閾値を見直す）。");
    }

    [Fact]
    public void MercyRuleDisabled_NeverSetsMercyEnded()
    {
        var ctx = new GameContext { MercyRuleEnabled = false };
        var strong = BuildTeam("強豪");
        var weak = BuildWeakTeam("弱小");
        for (ulong seed = 0; seed < 50; seed++)
        {
            var r = GameEngine.Play(strong, weak, ctx, new Xoshiro256Random(seed));
            Assert.False(r.MercyEnded);
        }
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

    // --- コールドゲーム統合テスト用: 平均チーム相手に大差がつく極端な弱小チーム ---

    private static Player WeakPosition(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 5, Power = 5, LaunchTendency = 50, Discipline = 5,
        Speed = 20, ArmStrength = 20, Fielding = 5, Catching = 5,
    };

    private static Player WeakPitcher(string name) => WeakPosition(FieldPosition.Pitcher) with
    {
        Name = name,
        Pitching = new PitcherAttributes { MaxVelocityKmh = 110, Control = 1, StaminaPitches = 45, PitchRank = 1 },
    };

    private static Team BuildWeakTeam(string name)
    {
        var order = new List<Player>
        {
            WeakPosition(FieldPosition.Catcher),
            WeakPosition(FieldPosition.FirstBase),
            WeakPosition(FieldPosition.SecondBase),
            WeakPosition(FieldPosition.ThirdBase),
            WeakPosition(FieldPosition.Shortstop),
            WeakPosition(FieldPosition.LeftField),
            WeakPosition(FieldPosition.CenterField),
            WeakPosition(FieldPosition.RightField),
            WeakPitcher(name + "P"),
        };
        return new Team
        {
            Name = name,
            BattingOrder = order,
            PitcherSlot = 8,
            Bullpen = new[] { WeakPitcher(name + "R1"), WeakPitcher(name + "R2") },
        };
    }
}

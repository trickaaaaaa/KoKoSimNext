using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// プレイヤー発動の伝令（タイム。設計書09 §3・issue #173）。攻守判定・消費・残数境界と、
/// 「伝令は乱数を消費しない＝同シード＋同操作で結果が再現する」決定論（不変条件#2）を保証する。
/// </summary>
public sealed class MatchProgressionTimeoutTests
{
    private static Player P(FieldPosition pos, string name) => new()
    {
        Position = pos, Name = name,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Player Rp(string name) => P(FieldPosition.Pitcher, name) with { Pitching = PitcherAttributes.LeagueAverage };

    private static Team Team(string n) => new()
    {
        Name = n,
        BattingOrder = new List<Player>
        {
            P(FieldPosition.Catcher, n + "捕"), P(FieldPosition.FirstBase, n + "一"),
            P(FieldPosition.SecondBase, n + "二"), P(FieldPosition.ThirdBase, n + "三"),
            P(FieldPosition.Shortstop, n + "遊"), P(FieldPosition.LeftField, n + "左"),
            P(FieldPosition.CenterField, n + "中"), P(FieldPosition.RightField, n + "右"),
            Rp(n + "先発P"),
        },
        PitcherSlot = 8,
        Bullpen = new[] { Rp(n + "二番手") },
    };

    private static MatchProgression NewProg(ulong seed)
        => new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed);

    // ===== 1. 攻守判定（表裏×ManagerIsAway） =====

    [Fact]
    public void FirstHalf_AwayIsBatting_HomeIsFielding()
    {
        var prog = NewProg(11UL);
        prog.Advance();   // 1回表（先攻 away の攻撃）に入る
        Assert.True(prog.IsTeamOnOffense(teamIsAway: true));
        Assert.False(prog.IsTeamOnOffense(teamIsAway: false));
    }

    // ===== 2. 消費（攻守で減る側が違う） =====

    [Fact]
    public void OffenseTimeout_WhileBatting_ConsumesOnlyTheOffenseCounter()
    {
        var prog = NewProg(11UL);
        prog.Advance();
        Assert.Equal(3, prog.OffenseTimeoutsLeft(teamIsAway: true));

        Assert.Equal(MatchProgression.PlayerTimeoutResult.Offense, prog.CallTimeout(teamIsAway: true));

        Assert.Equal(2, prog.OffenseTimeoutsLeft(teamIsAway: true));
        Assert.Equal(3, prog.DefenseTimeoutsLeft(teamIsAway: true));   // 守備伝令は減らない
    }

    [Fact]
    public void DefenseTimeout_WhileFielding_ConsumesOnlyTheDefenseCounter()
    {
        var prog = NewProg(11UL);
        prog.Advance();
        Assert.Equal(3, prog.DefenseTimeoutsLeft(teamIsAway: false));   // home は守備側

        Assert.Equal(MatchProgression.PlayerTimeoutResult.Defense, prog.CallTimeout(teamIsAway: false));

        Assert.Equal(2, prog.DefenseTimeoutsLeft(teamIsAway: false));
        Assert.Equal(3, prog.OffenseTimeoutsLeft(teamIsAway: false));   // 攻撃伝令は減らない
    }

    // ===== 3. 残数境界（0で不可・負にならない） =====

    [Fact]
    public void Timeout_Exhausted_ReturnsUnavailable_AndNeverGoesNegative()
    {
        var prog = NewProg(11UL);
        prog.Advance();
        for (var i = 0; i < 3; i++)
            Assert.Equal(MatchProgression.PlayerTimeoutResult.Offense, prog.CallTimeout(teamIsAway: true));

        Assert.Equal(0, prog.OffenseTimeoutsLeft(teamIsAway: true));
        Assert.False(prog.CanCallTimeout(teamIsAway: true));
        Assert.Equal(MatchProgression.PlayerTimeoutResult.Unavailable, prog.CallTimeout(teamIsAway: true));
        Assert.Equal(0, prog.OffenseTimeoutsLeft(teamIsAway: true));   // 0 のまま
    }

    [Fact]
    public void Timeout_AfterGameEnd_IsUnavailable()
    {
        var prog = NewProg(11UL);
        while (prog.Advance()) { /* 最後まで走らせる */ }
        Assert.False(prog.CanCallTimeout(teamIsAway: true));
        Assert.Equal(MatchProgression.PlayerTimeoutResult.Unavailable, prog.CallTimeout(teamIsAway: true));
    }

    // ===== 4. 決定論（同シード＋同操作 → 同結果。伝令は乱数を消費しない） =====

    [Theory]
    [InlineData(3UL)]
    [InlineData(31UL)]
    public void SameSeedAndSameTimeouts_ProduceTheSameResult(ulong seed)
    {
        static string Run(ulong s)
        {
            var prog = NewProg(s);
            var offUsed = 0;
            var defUsed = 0;
            while (prog.Advance())
            {
                // 攻撃中の away・守備中の away で、残があるうちに1つずつ伝令を試す（決まった操作列）。
                if (prog.IsTeamOnOffense(teamIsAway: true) && offUsed < 3 && prog.ConfirmedPlateAppearances > 4)
                {
                    if (prog.CallTimeout(teamIsAway: true) == MatchProgression.PlayerTimeoutResult.Offense) offUsed++;
                }
                else if (!prog.IsTeamOnOffense(teamIsAway: true) && defUsed < 3 && prog.ConfirmedPlateAppearances > 8)
                {
                    if (prog.CallTimeout(teamIsAway: true) == MatchProgression.PlayerTimeoutResult.Defense) defUsed++;
                }
            }
            return GameResultDigest.Sha256Of(prog.BuildResult());
        }

        Assert.Equal(Run(seed), Run(seed));
    }
}

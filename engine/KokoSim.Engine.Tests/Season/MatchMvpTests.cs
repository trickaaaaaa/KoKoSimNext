using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 今日のMVP（設計書02 §5.3c・issue #1745）。点数式・同点の決め方・ご褒美（成功体験ボーナスのちょうど3倍・調子一段）・
/// 勝った試合だけ、を固定する。数試合の実例（点数の目安）をテストの形で残す。
/// </summary>
public sealed class MatchMvpTests
{
    private static readonly SeasonCalendar Calendar = new();
    private static readonly GrowthStageTable Stages = new();
    private static readonly TrainingCoefficients C = new();

    private static DevelopingPlayer Player(int id, double condition = 0.0)
    {
        var p = new DevelopingPlayer { Id = id, Name = $"選手{id}", Grade = 2, GrowthType = GrowthType.Standard, ConditionValue = condition };
        foreach (var k in new[]
                 {
                     AbilityKind.Contact, AbilityKind.Power, AbilityKind.Fielding, AbilityKind.Catching,
                     AbilityKind.ThrowAccuracy, AbilityKind.Control, AbilityKind.PitchRank, AbilityKind.Stamina,
                 })
        {
            p.SetLevel(k, 40);
            p.SetCap(k, 90);
        }
        return p;
    }

    private static BattingLine Bat(int id, int order = 1, int ab = 4, int hits = 0, int doubles = 0, int triples = 0,
        int hr = 0, int rbi = 0, int runs = 0, int sb = 0)
        => new(order, FieldPosition.LeftField, $"選手{id}", ab, ab, hits, doubles, triples, hr, rbi, 0, 0, id,
            StolenBases: sb, Runs: runs) { Positions = new[] { FieldPosition.LeftField } };

    private static PitchingLine Pit(int id, int outs, int runs, int k, int faced = 30)
        => new($"選手{id}", outs, faced, 4, runs, k, 1, 100, id);

    private static GameResult Game(IReadOnlyList<BattingLine> bat, IReadOnlyList<PitchingLine> pit,
        int homeRuns = 5, int awayRuns = 1)
        => new()
        {
            AwayName = "相手", HomeName = "自校", AwayRuns = awayRuns, HomeRuns = homeRuns,
            InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
            HomeBatting = bat, HomePitching = pit,
        };

    private static MatchMvpPick? Choose(GameResult g, params DevelopingPlayer[] roster)
        => MatchMvp.Choose(g, managerIsAway: false, roster, C);

    [Fact]
    public void Batter_WhoHitAndDroveInRuns_IsMvpOverQuietPitcher()
    {
        // 3安打(1二塁打)2打点1得点 = 3 + 1 + 2 + 0.5 = 6.5。先発は6回3失点で 6+1.5−3=4.5。
        var g = Game(
            new[] { Bat(1, 1, hits: 3, doubles: 1, rbi: 2, runs: 1), Bat(2, 2, hits: 1, rbi: 1) },
            new[] { Pit(3, 18, 3, 5), Pit(4, 9, 0, 3) });
        var pick = Choose(g, Player(1), Player(2), Player(3), Player(4));

        Assert.NotNull(pick);
        Assert.Equal(1, pick!.PlayerId);
        Assert.Equal(6.5, pick.Score, 6);
        Assert.Equal("4打数3安打、2打点、1得点", MatchMvp.Describe(pick));
    }

    [Fact]
    public void CompleteGamePitcher_IsMvp_AndDescribedAsCompleteGame()
    {
        // 9回1失点10奪三振の完投 = 9 + 3 − 1 + 1 = 12。打者の最高は 4打数3安打2打点 = 5。
        var g = Game(
            new[] { Bat(1, 1, hits: 3, rbi: 2, runs: 1) },
            new[] { Pit(9, 27, 1, 10) });
        var pick = Choose(g, Player(1), Player(9));

        Assert.Equal(9, pick!.PlayerId);
        Assert.Equal(12.0, pick.Score, 6);
        Assert.True(pick.CompleteGame);
        Assert.False(pick.Shutout);
        Assert.Equal("9回1失点、10奪三振の完投", MatchMvp.Describe(pick));
    }

    [Fact]
    public void Shutout_GetsBothBonuses()
    {
        var g = Game(new[] { Bat(1, hits: 1) }, new[] { Pit(9, 27, 0, 6) }, homeRuns: 2, awayRuns: 0);
        var pick = Choose(g, Player(1), Player(9));

        Assert.Equal(9 + 1.8 + 1 + 1, pick!.Score, 6);
        Assert.Equal("9回を無失点、6奪三振の完封", MatchMvp.Describe(pick));
    }

    [Fact]
    public void StarterWhoDidNotFinish_IsNotCompleteGame_AndDescribedWithInnings()
    {
        var g = Game(new[] { Bat(1) }, new[] { Pit(9, 22, 1, 8), Pit(8, 5, 0, 1) });
        var pick = Choose(g, Player(1), Player(9), Player(8));

        Assert.Equal(9, pick!.PlayerId);
        Assert.False(pick.CompleteGame);
        Assert.Equal("7回1/3を投げて1失点、8奪三振", MatchMvp.Describe(pick));
    }

    [Fact]
    public void TwoWayPlayer_ScoresAreSummed_AndBothAreDescribed()
    {
        // 投 6回無失点4奪三振(6+1.2) + 打 2安打1本塁打(2+1+2) 2打点 = 7.2 + 5 + 2 = 14.2
        var g = Game(
            new[] { Bat(5, 4, hits: 2, hr: 1, rbi: 2) },
            new[] { Pit(5, 18, 0, 4), Pit(8, 9, 0, 2) });
        var pick = Choose(g, Player(5), Player(8));

        Assert.Equal(5, pick!.PlayerId);
        Assert.Equal(14.2, pick.Score, 6);
        Assert.Equal("6回を投げて無失点、4奪三振。打っても4打数2安打、1本塁打、2打点", MatchMvp.Describe(pick));
    }

    [Fact]
    public void Tie_BreaksByRbi_ThenInnings_ThenBattingOrder_AndIsRepeatable()
    {
        // 点数がちょうど同じ: A＝4安打(4)・打点0、B＝1安打(1)+1打点(1)+1得点…は異なるので組む。
        // A: 2安打 + 打点0 + 得点0 = 2。B: 打点2 = 2。→ 打点が多いBが勝つ。
        var a = Bat(1, 1, hits: 2);
        var b = Bat(2, 2, rbi: 2);
        var g1 = Game(new[] { a, b }, Array.Empty<PitchingLine>());
        Assert.Equal(2, Choose(g1, Player(1), Player(2))!.PlayerId);

        // 打点も同じなら打順の若い方（A＝1番・B＝2番とも 1安打1打点）。
        var g2 = Game(new[] { Bat(2, 2, hits: 1, rbi: 1), Bat(1, 1, hits: 1, rbi: 1) }, Array.Empty<PitchingLine>());
        Assert.Equal(1, Choose(g2, Player(1), Player(2))!.PlayerId);

        // 何度やっても同じ。
        for (var i = 0; i < 3; i++)
            Assert.Equal(1, Choose(g2, Player(2), Player(1))!.PlayerId);
    }

    [Fact]
    public void LossOrNoPositiveScore_HasNoMvp()
    {
        var g = Game(new[] { Bat(1, hits: 3) }, Array.Empty<PitchingLine>(), homeRuns: 1, awayRuns: 4);
        Assert.Null(Choose(g, Player(1)));

        var quiet = Game(new[] { Bat(1) }, Array.Empty<PitchingLine>());
        Assert.Null(Choose(quiet, Player(1)));
    }

    [Fact]
    public void ManagerAway_UsesAwaySide()
    {
        var g = new GameResult
        {
            AwayName = "自校", HomeName = "相手", AwayRuns = 3, HomeRuns = 2,
            InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
            AwayBatting = new[] { Bat(1, hits: 2) }, AwayPitching = Array.Empty<PitchingLine>(),
        };
        Assert.Equal(1, MatchMvp.Choose(g, managerIsAway: true, new[] { Player(1) }, C)!.PlayerId);
        Assert.Null(MatchMvp.Choose(g, managerIsAway: false, new[] { Player(1) }, C));
    }

    [Fact]
    public void PlayersNotOnRoster_AreIgnored()
    {
        var g = Game(new[] { Bat(1, hits: 3), Bat(2, 2, hits: 1) }, Array.Empty<PitchingLine>());
        Assert.Equal(2, Choose(g, Player(2))!.PlayerId);
    }

    // ---- ご褒美 ----

    private static void RunSuccess(GameResult g, DevelopingPlayer p, TournamentKind? kind)
        => MatchGrowthModel.ApplySuccess(g, managerIsAway: false, new[] { p }, kind, week: 10, Calendar, Stages, C);

    [Theory]
    [InlineData(TournamentKind.Summer)]
    [InlineData(TournamentKind.SummerKoshien)]
    public void BattingReward_IsExactlyThreeTimesTheSuccessBonus(TournamentKind kind)
    {
        var g = Game(new[] { Bat(1, hits: 3, rbi: 2) }, Array.Empty<PitchingLine>());

        var successOnly = Player(1);
        RunSuccess(g, successOnly, kind);
        var contactBonus = successOnly.Exp(AbilityKind.Contact);
        var powerBonus = successOnly.Exp(AbilityKind.Power);
        Assert.True(contactBonus > 0);

        var mvp = Player(1);
        var result = MatchMvp.Apply(g, false, new[] { mvp }, kind, 10, Calendar, Stages, C);

        Assert.NotNull(result);
        Assert.True(result!.Reward.Batting);
        Assert.False(result.Reward.Pitching);
        Assert.Equal(contactBonus * C.MvpRewardMult, mvp.Exp(AbilityKind.Contact), 6);
        Assert.Equal(powerBonus * C.MvpRewardMult, mvp.Exp(AbilityKind.Power), 6);
        Assert.Equal(3.0, C.MvpRewardMult);
        Assert.Equal(0.0, mvp.Exp(AbilityKind.Control));   // 投球の分野には何も入らない
        Assert.Equal(0.0, mvp.Exp(AbilityKind.Fielding));
    }

    [Fact]
    public void PitchingReward_IsExactlyThreeTimesTheSuccessBonus()
    {
        var g = Game(Array.Empty<BattingLine>(), new[] { Pit(9, 27, 1, 10) });

        var successOnly = Player(9);
        RunSuccess(g, successOnly, TournamentKind.Summer);
        Assert.True(successOnly.Exp(AbilityKind.Control) > 0);

        var mvp = Player(9);
        var result = MatchMvp.Apply(g, false, new[] { mvp }, TournamentKind.Summer, 10, Calendar, Stages, C);

        Assert.True(result!.Reward.Pitching);
        Assert.False(result.Reward.Batting);
        foreach (var k in new[] { AbilityKind.Control, AbilityKind.PitchRank, AbilityKind.Stamina })
            Assert.Equal(successOnly.Exp(k) * 3.0, mvp.Exp(k), 6);   // 能力ごとの伸びやすさも成功体験ボーナスと同じ経路
        Assert.Equal(0.0, mvp.Exp(AbilityKind.Contact));
    }

    [Fact]
    public void OnlyTheMvp_GetsTheReward()
    {
        var g = Game(new[] { Bat(1, 1, hits: 3, rbi: 2), Bat(2, 2, hits: 1) }, Array.Empty<PitchingLine>());
        var mvp = Player(1);
        var other = Player(2);
        MatchMvp.Apply(g, false, new[] { mvp, other }, TournamentKind.Summer, 10, Calendar, Stages, C);

        Assert.True(mvp.Exp(AbilityKind.Contact) > 0);
        Assert.Equal(0.0, other.Exp(AbilityKind.Contact));
        Assert.Equal(0.0, other.ConditionValue);
    }

    [Fact]
    public void Loss_ChangesNothing()
    {
        var g = Game(new[] { Bat(1, hits: 3, rbi: 2) }, Array.Empty<PitchingLine>(), homeRuns: 1, awayRuns: 4);
        var p = Player(1, condition: -0.3);
        var result = MatchMvp.Apply(g, false, new[] { p }, TournamentKind.Summer, 10, Calendar, Stages, C);

        Assert.Null(result);
        Assert.Equal(0.0, p.Exp(AbilityKind.Contact));
        Assert.Equal(-0.3, p.ConditionValue);
    }

    [Theory]
    [InlineData(-0.8, Condition.Poor)]
    [InlineData(-0.3, Condition.Normal)]
    [InlineData(0.0, Condition.Good)]
    [InlineData(0.3, Condition.Excellent)]
    [InlineData(0.9, Condition.Excellent)]
    public void ConditionRisesExactlyOneTier(double before, Condition expected)
    {
        var after = FormModel.RaiseOneTier(before);
        Assert.Equal(expected, FormModel.Quantize(after));
        Assert.True(after >= before);
    }

    [Fact]
    public void ConditionReward_IsReportedOnlyWhenTierMoved()
    {
        var g = Game(new[] { Bat(1, hits: 3, rbi: 2) }, Array.Empty<PitchingLine>());

        var normal = Player(1, condition: 0.0);
        var r1 = MatchMvp.Apply(g, false, new[] { normal }, TournamentKind.Summer, 10, Calendar, Stages, C);
        Assert.True(r1!.Reward.Condition);
        Assert.Equal(Condition.Good, FormModel.Quantize(normal.ConditionValue));

        var top = Player(1, condition: 0.9);
        var r2 = MatchMvp.Apply(g, false, new[] { top }, TournamentKind.Summer, 10, Calendar, Stages, C);
        Assert.False(r2!.Reward.Condition);
        Assert.Equal(0.9, top.ConditionValue);
    }

    [Fact]
    public void RewardText_NamesOnlyWhatMoved()
    {
        Assert.Equal("ミート・パワーに経験値、調子が上がった", MatchMvp.RewardText(new MatchMvpReward(true, false, true)));
        Assert.Equal("制球・キレ・スタミナに経験値", MatchMvp.RewardText(new MatchMvpReward(false, true, false)));
        Assert.Null(MatchMvp.RewardText(new MatchMvpReward(false, false, false)));
    }
}

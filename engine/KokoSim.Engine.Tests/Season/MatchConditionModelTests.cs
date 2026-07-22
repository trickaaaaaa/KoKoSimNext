using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 試合結果による ConditionValue フィードバック（設計書02 §3.3, issue #46）。
/// 好打/好投で+、不振/被弾/大敗で−、非出場者は不変、範囲は[-1,1]にクランプされることを検証する。
/// 乱数不使用＝決定論（不変条件#2）。
/// </summary>
public sealed class MatchConditionModelTests
{
    private static readonly FormCoefficients F = new();

    private static DevelopingPlayer Player(int id) => new() { Id = id, ConditionValue = 0.0 };

    private static BattingLine Bat(int sourceId, int atBats, int hits, int homeRuns = 0)
        => new(1, FieldPosition.LeftField, $"P{sourceId}", atBats, atBats, hits, 0, 0, homeRuns, 0, 0, 0, sourceId);

    private static PitchingLine Pitch(int sourceId, int outs, int runs, int battersFaced)
        => new($"P{sourceId}", outs, battersFaced, 0, runs, 0, 0, 0, sourceId);

    private static GameResult Game(
        IReadOnlyList<BattingLine>? homeBatting = null, IReadOnlyList<PitchingLine>? homePitching = null,
        int homeRuns = 1, int awayRuns = 0)
        => new()
        {
            AwayName = "相手", HomeName = "自校", AwayRuns = awayRuns, HomeRuns = homeRuns,
            InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
            HomeBatting = homeBatting ?? System.Array.Empty<BattingLine>(),
            HomePitching = homePitching ?? System.Array.Empty<PitchingLine>(),
        };

    // --- 好打/不振（打者） ---

    [Fact]
    public void MultiHitGame_RaisesCondition()
    {
        var p = Player(1);
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(homeBatting: new[] { Bat(p.Id, atBats: 4, hits: 3, homeRuns: 1) });

        MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.True(p.ConditionValue > 0, "好打の試合で調子が上がっていない");
    }

    [Fact]
    public void HitlessWithEnoughAtBats_LowersCondition()
    {
        var p = Player(1);
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(homeBatting: new[] { Bat(p.Id, atBats: 4, hits: 0) });

        MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.True(p.ConditionValue < 0, "無安打の試合で調子が下がっていない");
    }

    [Fact]
    public void HitlessWithFewAtBats_DoesNotPenalize()
    {
        // 規定打数（MatchHitlessMinAtBats=3）未満の無安打は「たまたま」であり減点しない。
        var p = Player(1);
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(homeBatting: new[] { Bat(p.Id, atBats: 2, hits: 0) });

        MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.Equal(0.0, p.ConditionValue);
    }

    // --- 好投/被弾（投手） ---

    [Fact]
    public void QualityStart_RaisesCondition()
    {
        var p = Player(1);
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(homePitching: new[] { Pitch(p.Id, outs: 21, runs: 1, battersFaced: 25) });

        MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.True(p.ConditionValue > 0, "好投で調子が上がっていない");
    }

    [Fact]
    public void Rocked_LowersCondition()
    {
        var p = Player(1);
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(homePitching: new[] { Pitch(p.Id, outs: 9, runs: 7, battersFaced: 20) });

        MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.True(p.ConditionValue < 0, "被弾で調子が下がっていない");
    }

    // --- 大敗（チーム全体） ---

    [Fact]
    public void BlowoutLoss_LowersConditionForAllParticipants()
    {
        var batter = Player(1);
        var pitcher = Player(2);
        var roster = new List<DevelopingPlayer> { batter, pitcher };
        // 打者は無安打未満打数（規定打数未満なので個人成績由来の減点はゼロ）、投手も好投/被弾いずれでもない中立成績。
        var game = Game(
            homeBatting: new[] { Bat(batter.Id, atBats: 2, hits: 0) },
            homePitching: new[] { Pitch(pitcher.Id, outs: 18, runs: 3, battersFaced: 22) },
            homeRuns: 0, awayRuns: 8); // 自校(Home)が大敗

        MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.True(batter.ConditionValue < 0, "大敗で打者の調子が下がっていない");
        Assert.True(pitcher.ConditionValue < 0, "大敗で投手の調子が下がっていない");
    }

    [Fact]
    public void CloseLoss_DoesNotTriggerBlowoutPenalty()
    {
        var p = Player(1);
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(homeBatting: new[] { Bat(p.Id, atBats: 2, hits: 0) }, homeRuns: 2, awayRuns: 3);

        MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.Equal(0.0, p.ConditionValue);
    }

    // --- 非出場者・範囲 ---

    [Fact]
    public void NonParticipants_AreUnaffected()
    {
        var starter = Player(1);
        var benchWarmer = Player(2);
        var roster = new List<DevelopingPlayer> { starter, benchWarmer };
        var game = Game(homeBatting: new[] { Bat(starter.Id, atBats: 4, hits: 3) });

        MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.Equal(0.0, benchWarmer.ConditionValue);
    }

    [Fact]
    public void RepeatedGreatGames_ClampAtPositiveOne()
    {
        var p = Player(1);
        p.ConditionValue = 0.95;
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(homeBatting: new[] { Bat(p.Id, atBats: 4, hits: 4, homeRuns: 2) });

        for (var i = 0; i < 20; i++) MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.InRange(p.ConditionValue, -1.0, 1.0);
        Assert.Equal(1.0, p.ConditionValue);
    }

    [Fact]
    public void RepeatedBadGames_ClampAtNegativeOne()
    {
        var p = Player(1);
        p.ConditionValue = -0.95;
        var roster = new List<DevelopingPlayer> { p };
        var game = Game(homeBatting: new[] { Bat(p.Id, atBats: 4, hits: 0) }, homeRuns: 0, awayRuns: 9);

        for (var i = 0; i < 20; i++) MatchConditionModel.Apply(game, managerIsAway: false, roster, F);

        Assert.InRange(p.ConditionValue, -1.0, 1.0);
        Assert.Equal(-1.0, p.ConditionValue);
    }

    [Fact]
    public void AwaySide_UsesAwayLines()
    {
        var p = Player(1);
        var roster = new List<DevelopingPlayer> { p };
        var game = new GameResult
        {
            AwayName = "自校", HomeName = "相手", AwayRuns = 2, HomeRuns = 1,
            InningsPlayed = 9, TotalPitches = 250, PitcherChanges = 0,
            AwayBatting = new[] { Bat(p.Id, atBats: 4, hits: 3) },
        };

        MatchConditionModel.Apply(game, managerIsAway: true, roster, F);

        Assert.True(p.ConditionValue > 0, "Away側の自校出場者の調子が上がっていない");
    }

    [Fact]
    public void SameInput_ProducesSameResult_IsDeterministic()
    {
        var a = Player(1);
        var b = Player(1);
        var rosterA = new List<DevelopingPlayer> { a };
        var rosterB = new List<DevelopingPlayer> { b };
        var game = Game(
            homeBatting: new[] { Bat(1, atBats: 4, hits: 2) },
            homePitching: new[] { Pitch(1, outs: 21, runs: 1, battersFaced: 25) });

        MatchConditionModel.Apply(game, managerIsAway: false, rosterA, F);
        MatchConditionModel.Apply(game, managerIsAway: false, rosterB, F);

        Assert.Equal(a.ConditionValue, b.ConditionValue);
    }
}

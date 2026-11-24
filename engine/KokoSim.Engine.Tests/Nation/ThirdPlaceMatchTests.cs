using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 3位決定戦（issue #1219・オーナー判断＝案A）。準決勝で負けた2校の3位/4位を強さ順ではなく試合で決める。
/// 自校が準決勝で負けたときは自校が3位決定戦を1試合戦う。順位は <see cref="KnockoutPlacement"/> が読む。
/// </summary>
public sealed class ThirdPlaceMatchTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static School Sch(int id, double strength) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength,
    };

    /// <summary>自校(id=1)＋others 校。強さをばらけさせる。</summary>
    private static List<School> Field(int others)
    {
        var list = new List<School> { Sch(1, 60) };
        for (var i = 0; i < others; i++) list.Add(Sch(100 + i, 30 + (i * 7) % 41));
        return list;
    }

    private static GameResult Box(string away, string home, int awayRuns, int homeRuns)
        => new()
        {
            AwayName = away, HomeName = home, AwayRuns = awayRuns, HomeRuns = homeRuns,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
        };

    /// <summary>自校(後攻)は常に勝つ。</summary>
    private sealed class AlwaysWinResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            => new(Box(opponent.Name, manager.Name, 1, 4), ManagerIsAway: false);

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null) => throw new System.NotSupportedException();
    }

    /// <summary>自校(後攻)は準決勝（RoundsRemaining==2の初戦）だけ負け、他は勝つ。3位決定戦（2回目の RoundsRemaining==2）の勝敗は指定どおり。</summary>
    private sealed class LoseSemifinalResolver : IPlayerMatchResolver
    {
        private readonly bool _winThirdPlace;
        private bool _semifinalPlayed;
        public LoseSemifinalResolver(bool winThirdPlace) => _winThirdPlace = winThirdPlace;

        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            var win = true;
            if (context?.RoundsRemaining == 2)
            {
                win = _semifinalPlayed && _winThirdPlace;
                _semifinalPlayed = true;
            }
            return new PlayerMatchDetail(
                win ? Box(opponent.Name, manager.Name, 1, 4) : Box(opponent.Name, manager.Name, 4, 1), ManagerIsAway: false);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            => new(new MatchProgression(MakeTeam(opponent.Name), MakeTeam(manager.Name),
                new GameContext { CaptureTimelines = true }, rng.Fork(2)), ManagerIsAway: false);

        private static Team MakeTeam(string name)
        {
            Player Pos(FieldPosition p) => new()
            {
                Position = p, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
                Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
            };
            var order = new List<Player>
            {
                Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
                Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
                Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
                Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
            };
            return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
        }
    }

    private static TournamentRunner Managed(IPlayerMatchResolver resolver, bool thirdPlace)
    {
        var field = Field(7);
        return new TournamentRunner(field, field[0], Coeff, new Xoshiro256Random(5), Schedule, "t",
            resolver, thirdPlaceMatch: thirdPlace);
    }

    // ===== 順位（観戦だけの大会） =====

    [Fact]
    public void Spectated_ThirdPlaceMatch_PlaysTheTwoSemifinalLosers_AndDecidesThirdAndFourth()
    {
        var field = Field(15);
        var byId = field.ToDictionary(s => s.Id);
        var runner = TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(11), Schedule, "t", thirdPlaceMatch: true);
        runner.DrainBracketResolution();

        var matches = runner.BuildBracketView().Matches;
        var third = Assert.Single(matches, m => m.IsThirdPlace);
        var semiLosers = matches.Where(m => !m.IsThirdPlace && m.RoundsRemaining == 2).Select(m => m.LoserId).ToHashSet();
        Assert.Equal(semiLosers, new HashSet<int> { third.WinnerId, third.LoserId });

        var placement = KnockoutPlacement.FromMatches(matches, id => byId[id]);
        Assert.Equal(16, placement.Count);
        Assert.Equal(placement.Select(s => s.Id).Distinct().Count(), placement.Count);
        Assert.Equal(third.WinnerId, placement[2].Id);
        Assert.Equal(third.LoserId, placement[3].Id);
    }

    [Fact]
    public void Spectated_ThirdPlaceMatch_IsDecidedByTheMatch_NotByStrengthOrder()
    {
        // 強さ順なら弱い方の準決勝敗退校が3位になることは無い。試合で決めるので、どこかのシードでは起きる。
        var upset = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var field = Field(15);
            var byId = field.ToDictionary(s => s.Id);
            var runner = TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(seed), Schedule, "t", thirdPlaceMatch: true);
            runner.DrainBracketResolution();
            var placement = KnockoutPlacement.FromMatches(runner.BuildBracketView().Matches, id => byId[id]);
            if (placement[2].Strength < placement[3].Strength) upset++;
        }
        Assert.True(upset > 0, "60大会で一度も下位の準決勝敗退校が3位にならない＝強さ順のまま");
    }

    [Fact]
    public void Spectated_WithoutThirdPlaceMatch_PlaysNoExtraGame_AndKeepsStrengthOrder()
    {
        var field = Field(15);
        var byId = field.ToDictionary(s => s.Id);
        var runner = TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(11), Schedule, "t");
        runner.DrainBracketResolution();

        var matches = runner.BuildBracketView().Matches;
        Assert.DoesNotContain(matches, m => m.IsThirdPlace);
        Assert.Equal(15, matches.Count);
        var placement = KnockoutPlacement.FromMatches(matches, id => byId[id]);
        Assert.True(placement[2].Strength >= placement[3].Strength);
    }

    [Fact]
    public void Spectated_ThirdPlaceMatch_IsDeterministic()
    {
        string Run()
        {
            var field = Field(15);
            var runner = TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(7), Schedule, "t", thirdPlaceMatch: true);
            runner.DrainBracketResolution();
            return string.Join(",", runner.BuildBracketView().Matches
                .Select(m => $"{m.RoundName}:{m.WinnerId}>{m.LoserId}:{m.WinnerScore}-{m.LoserScore}"));
        }
        Assert.Equal(Run(), Run());
    }

    // ===== 自校が準決勝で負けた場合 =====

    [Fact]
    public void ManagerLosingSemifinal_StaysActive_AndPlaysTheThirdPlaceMatch()
    {
        var r = Managed(new LoseSemifinalResolver(winThirdPlace: true), thirdPlace: true);
        Assert.True(r.PlayNextPlayerMatch().ManagerWon);            // 準々決勝
        var semi = r.PlayNextPlayerMatch();                          // 準決勝
        Assert.False(semi.ManagerWon);
        Assert.Equal("準決勝", semi.RoundName);

        // 準決勝で負けても大会は終わらない＝次戦は3位決定戦。試合日は決勝と同日・会場格は準決勝相当。
        Assert.False(r.Finished);
        Assert.True(r.PlayerActive);
        Assert.False(r.BracketResolutionPending);
        Assert.Equal(BracketMatch.ThirdPlaceRoundName, r.RoundName);
        Assert.Equal(2, r.NextMatchRoundsRemaining);
        Assert.Equal(Schedule.FinalDay(r.TotalRounds, r.FieldSize), r.NextMatchDay);
        var opponent = r.NextOpponent;
        Assert.NotNull(opponent);
        Assert.NotEqual(1, opponent!.Id);
        Assert.Contains(r.BuildBracketView().Matches, m => m.RoundsRemaining == 2 && m.LoserId == opponent.Id);
        Assert.DoesNotContain(r.BuildBracketView().Matches, m => m.IsThirdPlace);

        var third = r.PlayNextPlayerMatch();
        Assert.Equal(BracketMatch.ThirdPlaceRoundName, third.RoundName);
        Assert.True(third.ManagerWon);
        Assert.Equal(opponent.Id, third.OpponentId);
        Assert.NotNull(third.Detail);

        // 3位決定戦で自校の大会は終わる。決勝は裏試合として消化される。
        Assert.True(r.Finished);
        Assert.False(r.IsChampion);
        Assert.True(r.BracketResolutionPending);
        r.DrainBracketResolution();
        Assert.NotNull(r.ChampionName);

        var byId = Field(7).ToDictionary(s => s.Id);
        var placement = KnockoutPlacement.FromMatches(r.BuildBracketView().Matches, id => byId[id]);
        Assert.Equal(1, placement[2].Id);
        Assert.Equal(opponent.Id, placement[3].Id);
    }

    [Fact]
    public void ManagerLosingTheThirdPlaceMatch_FinishesFourth()
    {
        var r = Managed(new LoseSemifinalResolver(winThirdPlace: false), thirdPlace: true);
        r.PlayNextPlayerMatch();
        r.PlayNextPlayerMatch();
        var opponent = r.NextOpponent!;
        var third = r.PlayNextPlayerMatch();
        Assert.False(third.ManagerWon);
        r.DrainBracketResolution();

        var byId = Field(7).ToDictionary(s => s.Id);
        var placement = KnockoutPlacement.FromMatches(r.BuildBracketView().Matches, id => byId[id]);
        Assert.Equal(opponent.Id, placement[2].Id);
        Assert.Equal(1, placement[3].Id);
        var m = Assert.Single(r.BuildBracketView().Matches, x => x.IsThirdPlace);
        Assert.True(m.ManagerInvolved);
    }

    [Fact]
    public void ManagerLosingSemifinal_CanWatchTheThirdPlaceMatchLive()
    {
        var r = Managed(new LoseSemifinalResolver(winThirdPlace: true), thirdPlace: true);
        r.PlayNextPlayerMatch();
        r.PlayNextPlayerMatch();

        var live = r.BeginNextPlayerMatch();
        Assert.True(r.HasPendingLiveMatch);
        Assert.False(r.CanCaptureState);
        Assert.Equal(BracketMatch.ThirdPlaceRoundName, live.RoundName);
        Assert.Equal(2, live.RoundsRemaining);
        Assert.Same(live, r.BeginNextPlayerMatch());   // 取り残しは同じ進行体へ復帰する

        var outcome = r.CompleteNextPlayerMatch(Box(live.OpponentName, "自校", 2, 6));
        Assert.True(outcome.ManagerWon);
        Assert.Equal(BracketMatch.ThirdPlaceRoundName, outcome.RoundName);
        Assert.True(r.Finished);
        Assert.False(r.HasPendingLiveMatch);
        r.DrainBracketResolution();
        Assert.Single(r.BuildBracketView().Matches, m => m.IsThirdPlace && m.ManagerInvolved && m.WinnerId == 1);
    }

    [Fact]
    public void ManagerLosingSemifinal_WithoutThirdPlaceMatch_EndsTheTournament()
    {
        var r = Managed(new LoseSemifinalResolver(winThirdPlace: true), thirdPlace: false);
        r.PlayNextPlayerMatch();
        r.PlayNextPlayerMatch();
        Assert.True(r.Finished);
        Assert.False(r.PlayerActive);
        Assert.DoesNotContain(r.BuildBracketView().Matches, m => m.IsThirdPlace);
    }

    [Fact]
    public void ManagerWinningSemifinal_PlaysTheFinal_AndTheThirdPlaceMatchIsBackground()
    {
        var r = Managed(new AlwaysWinResolver(), thirdPlace: true);
        r.PlayNextPlayerMatch();
        var semi = r.PlayNextPlayerMatch();
        Assert.True(semi.ManagerWon);
        Assert.Equal("決勝", r.RoundName);
        var third = Assert.Single(r.BuildBracketView().Matches, m => m.IsThirdPlace);
        Assert.False(third.ManagerInvolved);
        Assert.True(r.PlayNextPlayerMatch().IsChampion);
    }

    [Fact]
    public void ManagerLosingSemifinal_WithoutDetailResolver_StillPlaysTheThirdPlaceMatch()
    {
        // resolver 無し（集計モデル）でも同じ流れ。自校が3位決定戦を戦って終わる。
        for (ulong seed = 1; seed <= 80; seed++)
        {
            var field = Field(7);
            var r = new TournamentRunner(field, field[0], Coeff, new Xoshiro256Random(seed), Schedule, "t",
                thirdPlaceMatch: true);
            while (!r.Finished && r.RoundName != BracketMatch.ThirdPlaceRoundName) r.PlayNextPlayerMatch();
            if (r.Finished) continue;   // 準決勝より前に負けた／優勝した
            var outcome = r.PlayNextPlayerMatch();
            Assert.Equal(BracketMatch.ThirdPlaceRoundName, outcome.RoundName);
            Assert.True(r.Finished);
            Assert.Contains(r.BuildBracketView().Matches, m => m.IsThirdPlace && m.ManagerInvolved);
            return;
        }
        Assert.Fail("80シードで一度も準決勝敗退にならない＝検証できない");
    }

    // ===== セーブ =====

    [Fact]
    public void SaveAndReload_WhileThirdPlaceMatchIsPending_ContinuesIdentically()
    {
        var r = Managed(new LoseSemifinalResolver(winThirdPlace: true), thirdPlace: true);
        r.PlayNextPlayerMatch();
        r.PlayNextPlayerMatch();
        Assert.Equal(BracketMatch.ThirdPlaceRoundName, r.RoundName);

        var state = r.CaptureState();
        Assert.True(state.ThirdPlaceMatch);
        var json = TournamentSaves.ToJson(new TournamentSaveState { Runner = state, Title = "t" });
        var restoredState = TournamentSaves.FromJson(JsonValue.Parse(json.ToString()))!.Runner;
        Assert.True(restoredState.ThirdPlaceMatch);

        var field = Field(7);
        var back = TournamentRunner.FromSaved(restoredState, field, field[0], Coeff, Schedule, new AlwaysWinResolver());
        Assert.True(back.PlayerActive);
        Assert.Equal(BracketMatch.ThirdPlaceRoundName, back.RoundName);
        Assert.Equal(r.NextOpponent!.Id, back.NextOpponent!.Id);
        Assert.Equal(r.NextMatchDay, back.NextMatchDay);
        Assert.Equal(2, back.NextMatchRoundsRemaining);

        var outcome = back.PlayNextPlayerMatch();
        Assert.Equal(BracketMatch.ThirdPlaceRoundName, outcome.RoundName);
        Assert.True(back.Finished);
        back.DrainBracketResolution();
        Assert.NotNull(back.ChampionName);
    }

    [Fact]
    public void SaveJson_WritesThirdPlaceOnlyWhenTheTournamentHasOne()
    {
        var field = Field(7);
        var plain = new TournamentRunner(field, field[0], Coeff, new Xoshiro256Random(1), Schedule, "t");
        var plainJson = TournamentSaves.ToJson(new TournamentSaveState { Runner = plain.CaptureState() });
        Assert.DoesNotContain("thirdPlace", plainJson.ToString());   // 旧版と同じ形
        Assert.False(TournamentSaves.FromJson(JsonValue.Parse(plainJson.ToString()))!.Runner.ThirdPlaceMatch);

        var withThird = new TournamentRunner(field, field[0], Coeff, new Xoshiro256Random(1), Schedule, "t",
            thirdPlaceMatch: true);
        var json = TournamentSaves.ToJson(new TournamentSaveState { Runner = withThird.CaptureState() });
        Assert.Contains("thirdPlace", json.ToString());
    }

    // ===== どの大会で行うか =====

    [Theory]
    [InlineData(TournamentKind.Autumn, true)]
    [InlineData(TournamentKind.AutumnDistrict, true)]
    [InlineData(TournamentKind.Regional, true)]
    [InlineData(TournamentKind.Summer, false)]
    [InlineData(TournamentKind.SummerKoshien, false)]
    [InlineData(TournamentKind.Jingu, false)]
    [InlineData(TournamentKind.SenbatsuKoshien, false)]
    public void ThirdPlaceRule_AppliesToTheAutumnQualifiersOnly(TournamentKind kind, bool expected)
        => Assert.Equal(expected, ThirdPlaceRule.AppliesTo(kind));

    // ===== 歴代大会結果 =====

    [Fact]
    public void TournamentHistory_ReachedRound_IgnoresTheThirdPlaceMatch()
    {
        var matches = new List<BracketMatch>
        {
            new("準決勝", 2, "A", "自校", 2, -1, 3, 1, true),
            new(BracketMatch.ThirdPlaceRoundName, 2, "自校", "B", -1, 3, 5, 2, true),
            new("決勝", 1, "A", "C", 2, 4, 2, 1, false),
        };
        var entry = new TournamentHistory().Fold(matches, TournamentKind.Autumn, 1, -1, new TournamentChampion(2, "A"));
        Assert.Equal("準決勝", entry.ReachedRoundName);
    }
}

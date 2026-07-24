using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Stats;

/// <summary>
/// 成績集計基盤（Stats/）の畳み込み算術・派生値・スコープ・勝敗判定・帰属を、手計算のボックススコアで検証する。
/// </summary>
public sealed class StatBookTests
{
    // ── 派生値（打撃） ──
    [Fact]
    public void BattingDerived_MatchesHandComputed()
    {
        var s = new BattingStatLine();
        // PA5 AB4 H2(内2B1・HR1・単0) RBI3 BB1 SO1
        s.Add(new BattingLine(3, FieldPosition.CenterField, "X",
            PlateAppearances: 5, AtBats: 4, Hits: 2, Doubles: 1, Triples: 0, HomeRuns: 1,
            Rbi: 3, Walks: 1, StrikeOuts: 1, SourceId: 1));

        Assert.Equal(1, s.Games);
        Assert.Equal(0, s.Singles);
        Assert.Equal(6, s.TotalBases);              // 2*1 + 4*1
        Assert.Equal(0.5, s.Average, 4);            // 2/4
        Assert.Equal(0.6, s.Obp, 4);                // (2+1)/(4+1)
        Assert.Equal(1.5, s.Slg, 4);                // 6/4
        Assert.Equal(2.1, s.Ops, 4);
    }

    [Fact]
    public void BattingAdd_AccumulatesAcrossGames_AndSkipsZeroPaGames()
    {
        var s = new BattingStatLine();
        s.Add(new BattingLine(1, FieldPosition.FirstBase, "X", 4, 4, 2, 0, 0, 0, 0, 0, 0, SourceId: 1));
        s.Add(new BattingLine(1, FieldPosition.FirstBase, "X", 3, 3, 1, 0, 0, 0, 0, 0, 0, SourceId: 1));
        s.Add(new BattingLine(1, FieldPosition.FirstBase, "X", 0, 0, 0, 0, 0, 0, 0, 0, 0, SourceId: 1)); // 出場なし

        Assert.Equal(2, s.Games);                   // PA=0 の試合は数えない
        Assert.Equal(7, s.AtBats);
        Assert.Equal(3, s.Hits);
    }

    // issue #91: 盗塁の個人集計。
    [Fact]
    public void BattingAdd_AccumulatesStolenBasesAndCaughtStealing()
    {
        var s = new BattingStatLine();
        s.Add(new BattingLine(1, FieldPosition.FirstBase, "X", 4, 4, 2, 0, 0, 0, 0, 0, 0, SourceId: 1,
            HitByPitches: 0, StolenBases: 2, CaughtStealing: 1));
        s.Add(new BattingLine(1, FieldPosition.FirstBase, "X", 3, 3, 1, 0, 0, 0, 0, 0, 0, SourceId: 1,
            HitByPitches: 0, StolenBases: 1, CaughtStealing: 0));

        Assert.Equal(3, s.StolenBases);
        Assert.Equal(1, s.CaughtStealing);
    }

    // issue #91: 失策の個人集計。
    [Fact]
    public void FieldingAdd_AccumulatesErrorsAcrossGames()
    {
        var s = new FieldingStatLine();
        s.Add(new FieldingLine(SourceId: 1, Position: FieldPosition.Shortstop, Name: "X", Errors: 1));
        s.Add(new FieldingLine(SourceId: 1, Position: FieldPosition.Shortstop, Name: "X", Errors: 2));

        Assert.Equal(3, s.Errors);
    }

    // ── 派生値（投手） ──
    [Fact]
    public void PitchingDerived_MatchesHandComputed()
    {
        var s = new PitchingStatLine();
        // 6回(18アウト) 被安打5 失点2(自責1・失策由来1) 奪三振7 与四球2 90球
        s.Add(new PitchingLine("P", Outs: 18, BattersFaced: 24, Hits: 5, Runs: 2, StrikeOuts: 7, Walks: 2, Pitches: 90,
                EarnedRuns: 1),
            started: true, win: true, loss: false);

        Assert.Equal(1, s.Games);
        Assert.Equal(1, s.GamesStarted);
        Assert.Equal(1, s.Wins);
        Assert.Equal(0, s.Losses);
        Assert.Equal("6", s.InningsText);
        Assert.Equal(1.5, s.Era, 4);                // 1*27/18（自責点, issue #69）
        Assert.Equal(3.0, s.Ra, 4);                 // 2*27/18（失点=RA）
        Assert.Equal(7.0 / 6.0, s.Whip, 4);         // (5+2)*3/18 = 21/18
        Assert.Equal(10.5, s.KPer9, 4);             // 7*27/18
    }

    // issue #180: 球種別被打の畳み込み（試合をまたいだ加算＋大会別アーカイブ相当の合算）。
    [Fact]
    public void PitchingAdd_AccumulatesBattingAgainstByPitch_AcrossGames()
    {
        var s = new PitchingStatLine();
        s.Add(new PitchingLine("P", Outs: 18, BattersFaced: 24, Hits: 5, Runs: 2, StrikeOuts: 7, Walks: 2, Pitches: 90,
                BattingAgainstByPitch: new Dictionary<PitchType, PitchTypeBattingLine>
                {
                    [PitchType.Fastball] = new(AtBats: 10, Hits: 3, HomeRuns: 1),
                    [PitchType.Slider] = new(AtBats: 4, Hits: 1, HomeRuns: 0),
                }),
            started: true, win: true, loss: false);
        s.Add(new PitchingLine("P", Outs: 15, BattersFaced: 20, Hits: 3, Runs: 1, StrikeOuts: 5, Walks: 1, Pitches: 75,
                BattingAgainstByPitch: new Dictionary<PitchType, PitchTypeBattingLine>
                {
                    [PitchType.Fastball] = new(AtBats: 6, Hits: 2, HomeRuns: 0),
                    [PitchType.Curve] = new(AtBats: 3, Hits: 1, HomeRuns: 0),
                }),
            started: true, win: false, loss: true);

        var byPitch = s.BattingAgainstByPitch;
        Assert.Equal(16, byPitch[PitchType.Fastball].AtBats);
        Assert.Equal(5, byPitch[PitchType.Fastball].Hits);
        Assert.Equal(1, byPitch[PitchType.Fastball].HomeRuns);
        Assert.Equal(0.3125, byPitch[PitchType.Fastball].Average, 4);
        Assert.Equal(4, byPitch[PitchType.Slider].AtBats);
        Assert.Equal(3, byPitch[PitchType.Curve].AtBats);
        Assert.False(byPitch.ContainsKey(PitchType.Changeup));

        var merged = new PitchingStatLine();
        merged.Merge(s);
        Assert.Equal(16, merged.BattingAgainstByPitch[PitchType.Fastball].AtBats);
        Assert.Equal(3, merged.BattingAgainstByPitch[PitchType.Curve].AtBats);
    }

    [Fact]
    public void InningsText_FormatsThirds()
    {
        var s = new PitchingStatLine();
        s.Add(new PitchingLine("P", Outs: 22, BattersFaced: 0, Hits: 0, Runs: 0, StrikeOuts: 0, Walks: 0, Pitches: 0),
            started: true, win: false, loss: false);
        Assert.Equal("7 1/3", s.InningsText);       // 22/3 = 7 余1
    }

    // ── 帰属・スコープ ──
    [Fact]
    public void FoldGame_AttributesManagerSideOnly_AndSkipsNullSourceId()
    {
        var book = new StatBook();
        var r = ResultManagerHome();
        book.FoldGame(r, managerIsAway: false, winPid: 2, losePid: null);

        Assert.NotNull(book.Get(1));                 // 自校打者
        Assert.NotNull(book.Get(2));                 // 自校先発
        Assert.Null(book.Get(99));                   // 相手校打者（SourceId=99）は自校帳簿に載らない
        Assert.Equal(2, book.Get(1)!.Batting.Hits);
        Assert.Equal(1, book.Get(2)!.Pitching.Wins);
    }

    // issue #91: 守備成績（失策）も打撃・投手と同じ帰属ルールで畳み込まれる。
    [Fact]
    public void FoldGame_AttributesFieldingErrorsToManagerSideOnly()
    {
        var book = new StatBook();
        var r = ResultManagerHome() with
        {
            HomeFielding = new[] { new FieldingLine(SourceId: 4, Position: FieldPosition.Shortstop, Name: "遊撃", Errors: 1) },
            AwayFielding = new[] { new FieldingLine(SourceId: 98, Position: FieldPosition.Shortstop, Name: "敵遊撃", Errors: 2) },
        };
        book.FoldGame(r, managerIsAway: false, winPid: 2, losePid: null);

        Assert.Equal(1, book.Get(4)!.Fielding.Errors);
        Assert.Null(book.Get(98));                   // 相手校の失策は自校帳簿に載らない
    }

    [Fact]
    public void PlayerStatStore_CareerPersists_TournamentResets()
    {
        var store = new PlayerStatStore();
        var r = ResultManagerHome();               // 自校=後攻・勝ち

        store.StartTournament();
        store.FoldGame(r, managerIsAway: false);
        Assert.Equal(2, store.Career.Get(1)!.Batting.Hits);
        Assert.Equal(2, store.CurrentTournament.Get(1)!.Batting.Hits);

        store.StartTournament();                    // 大会切替
        Assert.Null(store.CurrentTournament.Get(1)); // 今大会はリセット
        Assert.Equal(2, store.Career.Get(1)!.Batting.Hits); // 通算は残る
    }

    // ── 勝敗投手判定 ──
    [Fact]
    public void DecisionOfRecord_StarterGoesHalf_GetsDecision()
    {
        var r = ResultManagerHome();               // 先発18/救援9アウト=総27, 先発>=半分, 自校勝ち
        var (win, lose) = DecisionOfRecord.Resolve(r, managerIsAway: false);
        Assert.Equal(2, win);                       // 先発ID=2 が勝ち投手
        Assert.Null(lose);
    }

    [Fact]
    public void DecisionOfRecord_ShortStart_LastPitcherGetsDecision()
    {
        // 先発6アウト・救援21アウト=総27。先発は半分未満→最終登板投手が決定。自校=先攻で負け。
        var r = new GameResult
        {
            AwayName = "自校", HomeName = "相手", AwayRuns = 1, HomeRuns = 5,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 1,
            AwayPitching = new[]
            {
                new PitchingLine("先発", Outs: 6, BattersFaced: 0, Hits: 0, Runs: 0, StrikeOuts: 0, Walks: 0, Pitches: 0, SourceId: 10),
                new PitchingLine("救援", Outs: 21, BattersFaced: 0, Hits: 0, Runs: 0, StrikeOuts: 0, Walks: 0, Pitches: 0, SourceId: 11),
            },
        };
        var (win, lose) = DecisionOfRecord.Resolve(r, managerIsAway: true);
        Assert.Null(win);
        Assert.Equal(11, lose);                     // 最終登板=救援ID11が負け投手
    }

    [Fact]
    public void DecisionOfRecord_Tie_NoDecision()
    {
        var r = new GameResult
        {
            AwayName = "A", HomeName = "B", AwayRuns = 3, HomeRuns = 3,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            HomePitching = new[] { new PitchingLine("P", 27, 0, 0, 0, 0, 0, 0, SourceId: 5) },
        };
        var (win, lose) = DecisionOfRecord.Resolve(r, managerIsAway: false);
        Assert.Null(win);
        Assert.Null(lose);
    }

    // 自校=後攻(home)・勝ち。先発(ID2)18アウト・救援(ID3)9アウト。相手打者はSourceId=99。
    private static GameResult ResultManagerHome() => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 2, HomeRuns = 5,
        InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 1,
        HomeBatting = new[]
        {
            new BattingLine(3, FieldPosition.CenterField, "自1", 5, 4, 2, 1, 0, 1, 3, 1, 1, SourceId: 1),
        },
        AwayBatting = new[]
        {
            new BattingLine(1, FieldPosition.LeftField, "敵1", 4, 4, 1, 0, 0, 0, 0, 0, 1, SourceId: 99),
        },
        HomePitching = new[]
        {
            new PitchingLine("先発", 18, 24, 5, 2, 7, 2, 90, SourceId: 2),
            new PitchingLine("救援", 9, 12, 2, 0, 3, 1, 40, SourceId: 3),
        },
    };
}

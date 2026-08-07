using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 引き分け再試合（設計書05 §1.3, issue #253）の検証。延長15回で同点のまま打ち切られた試合を、
/// 自校戦（自動消化・観戦）と裏試合の三経路すべてが<b>同一の決着関数</b>（<c>DecideByReplay</c>）＝
/// 別Fork の再試合で決着させ、決着まで再試合しても引き分けなら規定（安打数→強さ→Id）で強制決着すること、
/// 自校が引き分けで無条件敗退しないこと、ブラケット表示が引き分けスコアのまま勝者を出さないこと、を担保する。
/// </summary>
public sealed class TournamentTieReplayTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new() { FirstRoundDay = 1, RoundGapDays = 3 };

    private static School Sch(int id, double strength) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength,
    };

    private static List<School> Field(double managerStrength, int others, double othersStrength)
    {
        var list = new List<School> { Sch(1, managerStrength) };
        for (var i = 0; i < others; i++) list.Add(Sch(100 + i, othersStrength));
        return list;
    }

    private static GameResult Box(string awayName, string homeName, int awayRuns, int homeRuns, int innings, int awayHits = 0, int homeHits = 0)
        => new()
        {
            AwayName = awayName, HomeName = homeName,
            AwayRuns = awayRuns, HomeRuns = homeRuns,
            AwayHits = awayHits, HomeHits = homeHits,
            InningsPlayed = innings, TotalPitches = 0, PitcherChanges = 0,
        };

    // ===== 自校戦（自動消化 ResolveRound） =====

    /// <summary>自校が後攻(home)。初戦は延長15回引き分け→再試合1回目で自校勝利。自校は無条件敗退しない。</summary>
    private sealed class SelfTieThenWinResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            var attempt = context?.TieReplay ?? 0;
            // 初戦(0): 3-3 で延長15回引き分け。再試合(>=1): 自校(home) 5-2 で決着。
            return attempt == 0
                ? new PlayerMatchDetail(Box(opponent.Name, manager.Name, 3, 3, 15), ManagerIsAway: false)
                : new PlayerMatchDetail(Box(opponent.Name, manager.Name, 2, 5, 9), ManagerIsAway: false);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null) => throw new System.NotSupportedException();
    }

    [Fact]
    public void SelfMatch_TieAtCap_ReplaysAndProducesWinner_NotUnconditionalLoss()
    {
        var r = new TournamentRunner(Field(60, 3, 55), Sch(1, 60), Coeff,
            new Xoshiro256Random(9), Schedule, "t", new SelfTieThenWinResolver());
        var outcome = r.PlayNextPlayerMatch();

        Assert.Equal(1, outcome.TieReplays);       // 初戦引き分け→再試合1回で決着
        Assert.True(outcome.ManagerWon);           // 引き分けでも無条件敗退しない（再試合で勝った）
        Assert.True(r.PlayerActive || r.IsChampion);
        Assert.Equal(5, outcome.ManagerScore);     // 表示は決着した再試合のスコア（引き分け3-3ではない）
        Assert.Equal(2, outcome.OpponentScore);
        Assert.NotNull(outcome.Detail);
        Assert.False(outcome.Detail!.Tied);        // Detail は決着済み試合
    }

    /// <summary>常に延長15回引き分け（3-3）。再試合上限まで決着しない＝規定（強さ→Id）で強制決着させる。</summary>
    private sealed class SelfAlwaysTieResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            => new(Box(opponent.Name, manager.Name, 3, 3, 15), ManagerIsAway: false);

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null) => throw new System.NotSupportedException();
    }

    [Fact]
    public void SelfMatch_AllReplaysTie_FallsBackToRegulation_StrongerManagerWins()
    {
        // 自校(home) が相手より強い＝規定決着（強さ）で自校が勝つ。無限ループにならず有限で終わる。
        var r = new TournamentRunner(Field(90, 3, 40), Sch(1, 90), Coeff,
            new Xoshiro256Random(4), Schedule, "t", new SelfAlwaysTieResolver());
        var outcome = r.PlayNextPlayerMatch();

        Assert.Equal(3, outcome.TieReplays);       // MaxTieReplays まで再試合しても引き分け
        Assert.True(outcome.ManagerWon);           // 規定決着（強さ）で自校勝利
    }

    // ===== 自校戦（観戦 CompleteRound） =====

    /// <summary>観戦の受け皿。BeginLive は最小進行体、再試合の Resolve は自校勝利を返す。</summary>
    private sealed class LiveTieResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            // 再試合（attempt>=1）は自校(home) 4-1 で決着。
            => new(Box(opponent.Name, manager.Name, 1, 4, 9), ManagerIsAway: false);

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

    [Fact]
    public void LiveMatch_WatchedTie_ReplaysToDecisive_ConsistentWithBracket()
    {
        var r = new TournamentRunner(Field(60, 3, 55), Sch(1, 60), Coeff,
            new Xoshiro256Random(2), Schedule, "t", new LiveTieResolver());
        var live = r.BeginNextPlayerMatch();
        Assert.True(r.HasPendingLiveMatch);

        // 観戦した試合が延長15回で 3-3 の引き分けだった、として終局結果を戻す（自校=後攻）。
        var watchedTie = Box(live.OpponentName, "桜丘", 3, 3, 15);
        var outcome = r.CompleteNextPlayerMatch(watchedTie);

        Assert.True(outcome.TieReplays >= 1);      // 観戦した引き分け→再試合で決着
        Assert.True(outcome.ManagerWon);
        Assert.Equal(4, outcome.ManagerScore);     // 大会進行は決着した再試合のスコア（引き分けではない）
        Assert.Equal(1, outcome.OpponentScore);
        Assert.False(outcome.Detail!.Tied);
    }

    // ===== 裏試合（ResolveBackgroundCard） =====

    /// <summary>初戦(TieReplay=0)は延長15回引き分け 2-2、再試合(>=1)は先攻 4-1 で決着。呼び出し回数を数える。</summary>
    private sealed class BgTieThenDecisiveResolver : IBackgroundMatchResolver
    {
        public int Calls;
        public GameResult Resolve(School away, School home, IRandomSource rng, TournamentMatchContext? context = null)
        {
            Calls++;
            var attempt = context?.TieReplay ?? 0;
            return attempt == 0
                ? Box(away.Name, home.Name, 2, 2, 15, awayHits: 5, homeHits: 5)
                : Box(away.Name, home.Name, 4, 1, 9, awayHits: 9, homeHits: 4);
        }
    }

    [Fact]
    public void BackgroundCard_TieAtCap_ReplaysToDecisive_BracketShowsDecisiveScore()
    {
        // playerResolver=null ＝ 自校カードも裏試合経路（ResolveBackgroundCard）を通る。2校＝1カード。
        var bg = new BgTieThenDecisiveResolver();
        var r = new TournamentRunner(Field(60, 1, 55), Sch(1, 60), Coeff,
            new Xoshiro256Random(1), Schedule, "t", playerResolver: null, backgroundResolver: bg);
        while (!r.Finished) r.PlayNextPlayerMatch();

        Assert.Equal(2, bg.Calls);   // 初戦（引き分け）＋再試合1回で決着＝ちょうど2試合

        var card = r.BuildBracketView().Matches.Single();
        Assert.Equal(4, card.WinnerScore);   // 決着した再試合のスコア（2-2の引き分けスコアではない＝#3破綻しない）
        Assert.Equal(1, card.LoserScore);
    }

    [Fact]
    public void BackgroundCard_TieReplay_IsDeterministic_SameSeedSameResult()
    {
        var a = new TournamentRunner(Field(60, 7, 55), Sch(1, 60), Coeff,
            new Xoshiro256Random(5), Schedule, "t", playerResolver: null, backgroundResolver: new BgTieThenDecisiveResolver());
        var b = new TournamentRunner(Field(60, 7, 55), Sch(1, 60), Coeff,
            new Xoshiro256Random(5), Schedule, "t", playerResolver: null, backgroundResolver: new BgTieThenDecisiveResolver());
        while (!a.Finished) a.PlayNextPlayerMatch();
        while (!b.Finished) b.PlayNextPlayerMatch();
        a.DrainBracketResolution();   // issue #529: 敗退後の残り消化は明示的に最後まで回す
        b.DrainBracketResolution();

        Assert.Equal(a.ChampionName, b.ChampionName);
        Assert.Equal(
            a.BuildBracketView().Matches.Select(m => $"{m.WinnerName} {m.WinnerScore}-{m.LoserScore} {m.LoserName}"),
            b.BuildBracketView().Matches.Select(m => $"{m.WinnerName} {m.WinnerScore}-{m.LoserScore} {m.LoserName}"));
    }

    // ===== メモ化リゾルバの再試合キー（無限ループ防止） =====

    private sealed class CountingBgResolver : IBackgroundMatchResolver
    {
        public int Calls;
        public GameResult Resolve(School away, School home, IRandomSource rng, TournamentMatchContext? context = null)
        {
            Calls++;
            var attempt = context?.TieReplay ?? 0;
            return Box(away.Name, home.Name, attempt, 0, 9);   // TieReplay ごとに違う結果
        }
    }

    [Fact]
    public void Memoizer_TieReplayInKey_ResolvesEachReplayOnce()
    {
        var inner = new CountingBgResolver();
        var memo = new MemoizingBackgroundResolver(inner);
        var a = Sch(1, 60);
        var b = Sch(2, 50);
        var rng = new Xoshiro256Random(1);
        var ledger = new TournamentPitchLedger();

        var r0 = memo.Resolve(a, b, rng, new TournamentMatchContext(1, 1, ledger, 0));
        var r0Again = memo.Resolve(a, b, rng, new TournamentMatchContext(1, 1, ledger, 0));   // 同キー＝キャッシュ命中
        var r1 = memo.Resolve(a, b, rng, new TournamentMatchContext(1, 1, ledger, 1));         // 別再試合回＝別キー

        Assert.Equal(2, inner.Calls);        // 再試合回0で1回、回1で1回。初戦の引き分けが返り続けない。
        Assert.Equal(0, r0.AwayRuns);
        Assert.Equal(0, r0Again.AwayRuns);   // 同キーはキャッシュ命中で同結果
        Assert.Equal(1, r1.AwayRuns);        // 別再試合回は別解決
    }
}

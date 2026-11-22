using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Stats;
using Xunit;
using NationModel = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 球数台帳とエース温存の入力が、経路によって欠けたり固定化したりしないこと（issue #1220）。
/// 台帳は記録側（<see cref="TournamentPitchLedger.RecordGame"/>）と読み側（<see cref="AceRestSelector"/>・
/// 回復モデル）でキーが揃っていて初めて効くので、各経路が同じ入口を通ることを固める。
/// </summary>
public sealed class PitchLedgerInputPathsTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();

    private static School Sch(int id, double strength = 50) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 1, Strength = strength,
    };

    private static GameResult Game(School away, School home, int awayRuns, int homeRuns, int pitches = 100, int innings = 9)
        => new()
        {
            AwayName = away.Name, HomeName = home.Name, AwayRuns = awayRuns, HomeRuns = homeRuns,
            InningsPlayed = innings, TotalPitches = 0, PitcherChanges = 0,
            AwayPitching = new[] { Line(pitches) },
            HomePitching = new[] { Line(pitches) },
        };

    // 背番号1（エース）が pitches 球。SourceId は全校共通の 1（AI校の校内選手IDと同じ潰れ方）にしておく。
    private static PitchingLine Line(int pitches)
        => new("投手", Outs: 27, BattersFaced: 30, Hits: 5, Runs: 2, StrikeOuts: 6, Walks: 2, Pitches: pitches,
            SourceId: 1, UniformNumber: 1);

    private static int AcePitchesBefore(TournamentPitchLedger ledger, int schoolId, int day)
        => ledger.PitchesWithin(PitcherLedgerKey.ForOpponent(schoolId, 1), day, windowDays: 1000);

    /// <summary>呼ばれた時点の台帳と文脈を取りつつ、先攻が勝つ固定スコアを返す裏試合解決器。</summary>
    private sealed class SpyResolver : IBackgroundMatchResolver
    {
        public readonly List<(School Away, School Home, TournamentMatchContext Context, int[] AcePitchesSoFar)> Calls = new();

        public GameResult Resolve(School away, School home, IRandomSource rng, TournamentMatchContext? context = null)
        {
            Calls.Add((away, home, context!, new[]
            {
                AcePitchesBefore(context!.Ledger, away.Id, context.MatchDay),
                AcePitchesBefore(context.Ledger, home.Id, context.MatchDay),
            }));
            return Game(away, home, awayRuns: 5, homeRuns: 2);
        }
    }

    // ===== 項目1: 観戦扱いの大会は全校を (校ID, 背番号) キーで記録する =====

    [Fact]
    public void SpectatedRunner_RecordsEverySchoolUnderTheKeyTheReadersUse()
    {
        var field = Enumerable.Range(0, 8).Select(i => Sch(10 + i)).ToList();
        var spy = new SpyResolver();
        TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(3), Schedule, "観戦", spy)
            .DrainBracketResolution();

        // 2回戦以降の各試合で、両校とも直前の試合の球数が台帳から引けている（先頭の1校だけゼロにならない）。
        var laterCalls = spy.Calls.Skip(4).ToList();
        Assert.NotEmpty(laterCalls);
        foreach (var c in laterCalls)
        {
            Assert.True(c.AcePitchesSoFar[0] > 0, $"校{c.Away.Id} の消耗が台帳に無い");
            Assert.True(c.AcePitchesSoFar[1] > 0, $"校{c.Home.Id} の消耗が台帳に無い");
        }
    }

    [Fact]
    public void RegionRunner_AcesPitchedInRound1_AreRestedInRound2_ForEverySchool()
    {
        // 温存確率＝エースの消耗負荷そのもの（他の項は0）にして、台帳が正しく読めた校だけが2回戦でエースを外す状況を作る。
        // 区画の先頭校だけ台帳キーが自校扱いだった旧実装では、その1校のエースが連投する。
        var ai = new EnemyAiCoefficients
        {
            AceRestMinTier = 0, AceRestBase = 0, AceRestTierGapWeight = 0, AceRestRoundsRemainingWeight = 0,
            AceRestFatigueWeight = 1.0, AceRestFatigueWindowDays = 1000, AceRestFatigueReferencePitches = 1.0,
            AceRestFloor = 0.0, AceRestCap = 1.0,
        };

        var checkedSchools = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var schools = Enumerable.Range(0, 8).Select(i => Sch(i, 40 + i * 3)).ToList();
            var nation = new NationModel(new[] { new Prefecture(1, "県1", 8) }, schools);
            var rosters = new NationRosters(new AiRosterDeps { EnemyAi = ai });
            var stats = new NationTournamentStats();
            var result = NationalTournamentEngine.RunSummer(
                nation, rosters, new GameContext(), Coeff, Schedule, yearIndex: 1, stats, new Xoshiro256Random(seed)).Single();

            foreach (var s in schools)
            {
                var book = stats.ForSchool(s.Id);
                if (book is null) continue;
                var won = result.Wins.TryGetValue(s.Id, out var w) ? w : 0;
                var played = won + (s.Id == result.ChampionId ? 0 : 1);
                var started = book.Players.Values.Sum(p => p.Pitching.GamesStarted);
                if (played < 2 || started != played) continue;   // 引き分け再試合が入った校は試合数が合わない＝除く

                var maxStarts = book.Players.Values.Max(p => p.Pitching.GamesStarted);
                Assert.True(maxStarts <= played - 1, $"seed{seed} 校{s.Id}: 全{played}試合のうち{maxStarts}試合を同じ投手が先発");
                checkedSchools++;
            }
        }
        Assert.True(checkedSchools > 0);
    }

    // ===== 項目2: 予選成績の打ち直しは試合ごとに台帳へ記録する =====

    [Fact]
    public void QualifierReplay_RecordsEachGameIntoItsLedger()
    {
        var self = Sch(1);
        var byId = new[] { self, Sch(2), Sch(3), Sch(4) }.ToDictionary(s => s.Id);
        var path = new List<ChampionMatchup> { new(2, 3), new(3, 2), new(4, 1) };
        var spy = new SpyResolver();

        QualifierStatsSim.ReplayPath(
            self.Id, path, id => byId.TryGetValue(id, out var s) ? s : null, spy,
            (_, _, _) => { }, Schedule, QualifierStatsSim.RoundsFor(8), yearIndex: 1, new Xoshiro256Random(5));

        Assert.Equal(3, spy.Calls.Count);
        var selfPitchesBefore = spy.Calls.Select(c => c.Away.Id == self.Id ? c.AcePitchesSoFar[0] : c.AcePitchesSoFar[1]).ToList();
        Assert.Equal(0, selfPitchesBefore[0]);
        Assert.True(selfPitchesBefore[1] > 0);
        Assert.True(selfPitchesBefore[2] > selfPitchesBefore[1]);
    }

    // ===== 項目3: 引き分け再試合は全試合の球数を記録する =====

    private sealed class TieThenDecideResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            // 自校は後攻。初戦(0)は延長15回で引き分け・再試合(>=1)で自校勝利。
            var g = (context?.TieReplay ?? 0) == 0
                ? Game(opponent, manager, 3, 3, pitches: 190, innings: 15)
                : Game(opponent, manager, 2, 5, pitches: 120);
            return new PlayerMatchDetail(g, ManagerIsAway: false);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null) => throw new System.NotSupportedException();
    }

    [Fact]
    public void ManagerMatchTieReplay_RecordsBothGamesPitches()
    {
        var manager = Sch(1, 80);
        var field = new List<School> { manager, Sch(2, 40), Sch(3, 40), Sch(4, 40) };
        var spy = new SpyResolver();
        var runner = new TournamentRunner(field, manager, Coeff, new Xoshiro256Random(9), Schedule, "t",
            new TieThenDecideResolver(), spy);

        var outcome = runner.PlayNextPlayerMatch();
        Assert.Equal(1, outcome.TieReplays);

        // 準決勝の裏カード（自校以外の1試合）が引いた台帳は大会の台帳そのもの。自校は育成選手ID（SourceId）キー。
        Assert.Single(spy.Calls);
        var ledger = spy.Calls[0].Context.Ledger;
        Assert.Equal(190 + 120, ledger.PitchesWithin(PitcherLedgerKey.ForPlayer(1), currentDay: 10_000, windowDays: 10_000));
    }

    // ===== 項目4: エース温存の抽選は大会ごとに変わる =====

    [Fact]
    public void AceRestSalt_DiffersByTournamentRng_AndIsStableForTheSameOne()
    {
        var a = TournamentMatchContext.AceRestSaltOf(new Xoshiro256Random(1));
        Assert.NotEqual(a, TournamentMatchContext.AceRestSaltOf(new Xoshiro256Random(2)));
        Assert.Equal(a, TournamentMatchContext.AceRestSaltOf(new Xoshiro256Random(1)));
    }

    [Fact]
    public void AceRestSelector_DifferentSalt_ChangesTheDrawForTheSameSchoolAndDay()
    {
        var ai = new EnemyAiCoefficients { AceRestMinTier = 0, AceRestBase = 0.5, AceRestFloor = 0.5, AceRestCap = 0.5 };
        bool Rests(int id, ulong salt)
            => AceRestSelector.ShouldRestAce(Sch(id), new AceRestContext(Tier.G, 3, 4, null, salt), ai);

        var ids = Enumerable.Range(0, 200).ToList();
        Assert.Contains(ids, id => Rests(id, 0) != Rests(id, 0xDEADBEEF));   // 塩が違えば同じ校・同じ日でも抽選値が変わる
        Assert.InRange(ids.Count(id => Rests(id, 0)), 60, 140);              // 確率0.5の抽選であること自体は変わらない
        Assert.InRange(ids.Count(id => Rests(id, 0xDEADBEEF)), 60, 140);
    }

    [Fact]
    public void Runner_PassesDifferentAceRestSaltToTournamentsWithDifferentRng()
    {
        TournamentMatchContext FirstContext(ulong seed)
        {
            var spy = new SpyResolver();
            var field = Enumerable.Range(0, 4).Select(i => Sch(20 + i)).ToList();
            TournamentRunner.Spectated(field, Coeff, new Xoshiro256Random(seed), Schedule, "t", spy).DrainBracketResolution();
            return spy.Calls[0].Context;
        }

        Assert.NotEqual(FirstContext(1).AceRestSalt, FirstContext(2).AceRestSalt);
        Assert.Equal(FirstContext(1).AceRestSalt, FirstContext(1).AceRestSalt);
    }

    // ===== 項目5: リーグ戦の温存判断は実際の残り節数を見る =====

    [Fact]
    public void BlockLeague_AceRestSeesRemainingLeagueRounds_NotTheConstantUsedForVenueAndMercy()
    {
        var blocks = new List<IReadOnlyList<School>> { Enumerable.Range(0, 4).Select(i => Sch(30 + i)).ToList() };
        var spy = new SpyResolver();
        BlockLeagueRunner.Spectated(blocks, 2, Coeff, new Xoshiro256Random(8), Schedule, "リーグ", spy)
            .DrainBracketResolution();

        Assert.Equal(6, spy.Calls.Count);   // 4校の総当たり＝3節×2試合
        Assert.All(spy.Calls, c => Assert.Equal(BlockLeagueRunner.LeagueRoundsRemaining, c.Context.RoundsRemaining));
        Assert.Equal(new[] { 3, 3, 2, 2, 1, 1 }, spy.Calls.Select(c => c.Context.RestRoundsRemaining).ToArray());
        Assert.Equal(1, AceRestContext.From(spy.Calls[^1].Context, Tier.G)!.RoundsRemaining);   // 最終節＝もう温存しない
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 約束の見張り（設計書04 §2.4・<see cref="MatchPromiseGuard"/>）。采配AIが約束の相手に代打・バント・途中出場・
/// 登板をさせないこと、控えに投げられる子がいなければ野手をマウンドへ回すこと、見張りが無ければ従来どおりであること。
/// </summary>
public sealed class MatchPromiseGuardTests
{
    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher,
    };

    private static Team MakeTeam(IEnumerable<Player> bench, MatchPromiseGuard? guard)
    {
        var order = Positions.Select((p, i) => new Player
        {
            Position = p, Name = p + "先発", SourceId = i + 1,
            Pitching = p == FieldPosition.Pitcher ? PitcherAttributes.LeagueAverage : new PitcherAttributes { Control = 10 + i },
        }).ToList();
        return new Team
        {
            Name = "テスト校", BattingOrder = order, PitcherSlot = 8, Reserves = bench.ToList(), PromiseGuard = guard,
        };
    }

    private static Player Reserve(int id, FieldPosition pos) => new() { Position = pos, Name = "控え" + id, SourceId = id };

    private static MatchPromiseGuard Guard(int[]? noPinchHitFor = null, int[]? noBunt = null, int[]? noEntry = null,
        int[]? noPitch = null)
        => new(noPinchHitFor ?? new int[0], noBunt ?? new int[0], noEntry ?? new int[0], noPitch ?? new int[0]);

    // ── 控えと継投の候補 ──────────────────────────────────

    [Fact]
    public void WithoutAGuard_EverythingIsAsBefore()
    {
        var bench = new[] { Reserve(20, FieldPosition.Pitcher), Reserve(21, FieldPosition.LeftField) };
        var state = new TeamState(MakeTeam(bench, guard: null));
        Assert.Same(state.Reserves, state.EligibleReserves);
        Assert.Equal(state.ReliefRankingCandidates, state.EligibleReliefCandidates);

        Assert.True(state.TryChangePitcherKeepingPromises());
        Assert.Equal(20, state.CurrentPitcher.SourceId);   // 従来の既定＝名簿順で最初の投手の控え
    }

    [Fact]
    public void BlockedReserves_AreHiddenFromTheBrain()
    {
        var bench = new[] { Reserve(20, FieldPosition.Pitcher), Reserve(21, FieldPosition.LeftField) };
        var state = new TeamState(MakeTeam(bench, Guard(noEntry: new[] { 21 }, noPitch: new[] { 20 })));
        Assert.Equal(new int?[] { 20 }, state.EligibleReserves.Select(p => p.SourceId));
        Assert.DoesNotContain(state.EligibleReliefCandidates, p => p.SourceId is 20 or 21);
    }

    [Fact]
    public void Relief_SkipsAPromisedPitcher_ForTheNextReserve()
    {
        var bench = new[] { Reserve(20, FieldPosition.Pitcher), Reserve(21, FieldPosition.Pitcher) };
        var state = new TeamState(MakeTeam(bench, Guard(noPitch: new[] { 20 })));
        Assert.True(state.TryChangePitcherKeepingPromises());
        Assert.Equal(21, state.CurrentPitcher.SourceId);
    }

    [Fact]
    public void Relief_MovesAFielderToTheMound_WhenNoReserveMayPitch()
    {
        var bench = new[] { Reserve(20, FieldPosition.Pitcher), Reserve(21, FieldPosition.LeftField) };
        var state = new TeamState(MakeTeam(bench, Guard(noEntry: new[] { 21 }, noPitch: new[] { 20 })));
        var starter = state.CurrentPitcher;

        Assert.True(state.TryChangePitcherKeepingPromises());

        Assert.InRange(state.CurrentPitcher.SourceId ?? 0, 1, 8);              // 守っていた野手が投げる
        Assert.Equal(new int?[] { 20, 21 }, state.Reserves.Select(p => p.SourceId));   // 控えは誰も出ていない
        Assert.Contains(state.CurrentLineup, p => p.SourceId == starter.SourceId);   // 元の投手は守備へ回る
    }

    // ── 試合を通して見張りが効くこと ──────────────────────────

    [Fact]
    public void AiManagedGames_NeverBreakTheGuardedPromises()
    {
        const int games = 40;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        long entriesWithout = 0, sacWithout = 0, reserveReliefWithout = 0, fielderReliefWith = 0;
        Parallel.For(0, games, g =>
        {
            var (with, reserveIds, lineupIds) = Play(g, guarded: true);
            if (with.HomeBatting.Any(l => l.Entry != BoxEntry.Starter)) failures.Add($"g{g}: 途中出場あり");
            if (with.HomeBatting.Sum(l => l.SacrificeHits) > 0) failures.Add($"g{g}: 犠打あり");
            if (with.HomePitching.Any(l => l.SourceId is { } id && reserveIds.Contains(id))) failures.Add($"g{g}: 控えが登板");
            if (with.HomePitching.Skip(1).Any(l => l.SourceId is { } id && lineupIds.Contains(id)))
                System.Threading.Interlocked.Increment(ref fielderReliefWith);

            // 対照: 同じ試合を見張り無しで回すと、AI は実際に代打・犠打・控えの継投を使う（＝上の検査が空振りでない）。
            var (without, _, _) = Play(g, guarded: false);
            System.Threading.Interlocked.Add(ref entriesWithout, without.HomeBatting.Count(l => l.Entry != BoxEntry.Starter));
            System.Threading.Interlocked.Add(ref sacWithout, without.HomeBatting.Sum(l => l.SacrificeHits));
            System.Threading.Interlocked.Add(ref reserveReliefWithout,
                without.HomePitching.Count(l => l.SourceId is { } id && reserveIds.Contains(id)));
        });
        Assert.True(failures.IsEmpty, string.Join(" / ", failures));
        Assert.True(entriesWithout > 0, "対照の試合で途中出場が一度も無い");
        Assert.True(sacWithout > 0, "対照の試合で犠打が一度も無い");
        Assert.True(reserveReliefWithout > 0, "対照の試合で控えの継投が一度も無い");
        Assert.True(fielderReliefWith > 0, "見張りのある試合で野手がマウンドへ回っていない");
    }

    private static (GameResult Result, int[] ReserveIds, int[] LineupIds) Play(int g, bool guarded)
    {
        var rng = new Xoshiro256Random(0x9A0D_0000UL + (ulong)g);
        var home = WithIds(StrengthTeamFactory.Create(55, "自校", rng));
        var lineupIds = home.BattingOrder.Select(p => p.SourceId!.Value).ToArray();
        var reserveIds = home.Reserves.Select(p => p.SourceId!.Value).ToArray();
        // 見張りあり: 控え全員が「出さないで」・打者全員が「代打を出さない／バントさせない」
        // ＝AIは控えを一切使えず、継投は野手を回すしかない。
        home = home with
        {
            Tactics = new AiTacticsBrain(new AiProfile(90, 6, g % 2 == 0 ? SchoolStyle.SmallBall : SchoolStyle.TotalBaseball)),
            PromiseGuard = guarded ? Guard(noPinchHitFor: lineupIds, noBunt: lineupIds, noEntry: reserveIds) : null,
        };
        var away = StrengthTeamFactory.Create(70, "相手", rng) with
        {
            Tactics = new AiTacticsBrain(new AiProfile(60, 5, SchoolStyle.Standard)),
        };
        return (GameEngine.Play(away, home, new GameContext(), rng), reserveIds, lineupIds);
    }

    [Fact]
    public void GuardFor_BuildsOnlyFromLivePromises()
    {
        var roster = new List<DevelopingPlayer>
        {
            new() { Id = 1, Grade = 1, Promise = ScoutPromiseKind.NoBunt, PromiseStatus = ScoutPromiseStatus.Active },
            new() { Id = 2, Grade = 1, Promise = ScoutPromiseKind.RestFirstSummer, PromiseStatus = ScoutPromiseStatus.Active },
            new() { Id = 3, Grade = 2, Promise = ScoutPromiseKind.NoConsecutiveDays, PromiseStatus = ScoutPromiseStatus.Active,
                    LastOfficialPitchStamp = 5000 },
            new() { Id = 4, Grade = 1, Promise = ScoutPromiseKind.NoPinchHitter, PromiseStatus = ScoutPromiseStatus.Broken },
        };
        var g = ScoutPromiseJudge.GuardFor(roster, TournamentKind.Summer, 5001)!;
        Assert.True(g.BlocksBunt(new Player { SourceId = 1 }));
        Assert.True(g.BlocksEntry(new Player { SourceId = 2 }));
        Assert.True(g.BlocksPitching(new Player { SourceId = 3 }));
        Assert.False(g.BlocksPinchHitFor(new Player { SourceId = 4 }));   // 破った約束はもう見張らない

        Assert.Null(ScoutPromiseJudge.GuardFor(roster, TournamentKind.SenbatsuKoshien, 9000));   // センバツは見張らない
    }

    private static Team WithIds(Team t)
    {
        var id = 1;
        return t with
        {
            BattingOrder = t.BattingOrder.Select(p => p with { SourceId = id++ }).ToList(),
            Reserves = t.Reserves.Select(p => p with { SourceId = id++ }).ToList(),
            StartingPitcher = t.StartingPitcher is { } sp ? sp with { SourceId = id++ } : null,
        };
    }
}

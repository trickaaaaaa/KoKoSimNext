using System;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Practice;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Practice;

/// <summary>
/// 練習試合（設計書03 §週ターン③・設計書04 §名声）の受諾判定・週1制約・資金・決定論、
/// および成績スコープ分離（通算に入り公式戦通算には入らない）を検証する。
/// </summary>
public sealed class PracticeMatchTests
{
    private static readonly PracticeMatchCoefficients C = new();

    // ── 受諾判定（名声/ティア差） ──

    [Fact]
    public void AcceptChance_SameTier_UsesBasePlusFame()
    {
        Assert.Equal(C.BaseAccept, PracticeMatchModel.AcceptChance(Tier.D, Tier.D, fame: 0, C), 6);
        Assert.Equal(Math.Min(C.MaxAccept, C.BaseAccept + C.FameWeight),
            PracticeMatchModel.AcceptChance(Tier.D, Tier.D, fame: 100, C), 6);
    }

    [Fact]
    public void AcceptChance_StrongerOpponent_DropsPerTier_AndFameRecovers()
    {
        var gap2 = PracticeMatchModel.AcceptChance(Tier.E, Tier.C, fame: 0, C);
        Assert.Equal(C.BaseAccept - 2 * C.TierGapPenalty, gap2, 6);

        // 名声が高ければ格上でも申込が通りやすくなる（設計書04 §名声の効果）
        Assert.True(PracticeMatchModel.AcceptChance(Tier.E, Tier.C, fame: 90, C) > gap2);
    }

    [Fact]
    public void AcceptChance_WeakerOpponent_NoPenalty_AndClamped()
    {
        Assert.Equal(C.BaseAccept, PracticeMatchModel.AcceptChance(Tier.A, Tier.F, fame: 0, C), 6);
        Assert.Equal(C.MaxAccept, PracticeMatchModel.AcceptChance(Tier.A, Tier.F, fame: 100, C), 6);
        Assert.Equal(C.MinAccept, PracticeMatchModel.AcceptChance(Tier.G, Tier.S, fame: 0, C), 6);
    }

    // ── 週1制約・資金 ──

    [Fact]
    public void Request_ConsumesWeeklySlotAndFunds_ThenBlocksSameWeek()
    {
        var s = new PracticeMatchScheduler(C);
        var manager = new Manager { Fame = 100, Funds = 10 };
        var resolver = new StubResolver();

        var first = s.Request(manager, School(60), School(40), week: 3, resolver, Rng(1));
        Assert.True(first.Played);
        Assert.Equal(9, manager.Funds, 6);          // 1万円減算
        Assert.Equal(3, s.LastPlayedWeek);

        var second = s.Request(manager, School(60), School(40), week: 3, resolver, Rng(2));
        Assert.False(second.Played);
        Assert.Equal(PracticeMatchRejection.AlreadyPlayedThisWeek, second.Rejection);
        Assert.Equal(9, manager.Funds, 6);          // 拒否時は減らない

        var nextWeek = s.Request(manager, School(60), School(40), week: 4, resolver, Rng(1));
        Assert.True(nextWeek.Played);               // 週が変われば再び申込可能
    }

    [Fact]
    public void Request_InsufficientFunds_IsRejectedByEngine()
    {
        var s = new PracticeMatchScheduler(C);
        var manager = new Manager { Fame = 100, Funds = 0.5 };

        Assert.Equal(PracticeMatchRejection.InsufficientFunds, s.CanRequest(manager, week: 1));
        var r = s.Request(manager, School(60), School(40), week: 1, new StubResolver(), Rng(1));
        Assert.False(r.Played);
        Assert.Equal(PracticeMatchRejection.InsufficientFunds, r.Rejection);
        Assert.Null(s.LastPlayedWeek);
        Assert.Equal(0.5, manager.Funds, 6);
    }

    [Fact]
    public void Request_Declined_CostsNothingAndKeepsSlot()
    {
        var s = new PracticeMatchScheduler(C);
        var manager = new Manager { Fame = 0, Funds = 10 };
        // 自校G・相手S＝受諾確率は下限0.02。乱数0.9固定なら必ず断られる。
        var r = s.Request(manager, School(20), School(95), week: 1, new StubResolver(), new FixedRandom(0.9));

        Assert.False(r.Played);
        Assert.Equal(PracticeMatchRejection.Declined, r.Rejection);
        Assert.Equal(10, manager.Funds, 6);
        Assert.Null(s.LastPlayedWeek);
        Assert.Null(r.Detail);
    }

    // ── 決定論（不変条件#2） ──

    [Fact]
    public void Request_IsDeterministic_ForSameSeed()
    {
        static (bool Played, int Runs) Run(ulong seed)
        {
            var s = new PracticeMatchScheduler(C);
            var m = new Manager { Fame = 50, Funds = 10 };
            var r = s.Request(m, School(55), School(58), week: 7, new StubResolver(), new Xoshiro256Random(seed));
            return (r.Played, r.Detail?.Result.HomeRuns ?? -1);
        }

        Assert.Equal(Run(4242), Run(4242));
    }

    // ── 成績スコープ分離 ──

    [Fact]
    public void PracticeMatch_CountsInCareerOnly_NotOfficialNorTournament()
    {
        var store = new PlayerStatStore();
        store.StartTournament();

        store.FoldGame(Box(), managerIsAway: false, isOfficial: true);
        store.FoldGame(Box(), managerIsAway: false, isOfficial: false);   // 練習試合

        Assert.Equal(4, store.Career.Get(1)!.Batting.Hits);               // 2試合ぶん
        Assert.Equal(2, store.Official.Get(1)!.Batting.Hits);             // 公式戦のみ
        Assert.Equal(2, store.CurrentTournament.Get(1)!.Batting.Hits);
        Assert.Equal(2, store.Career.Get(2)!.Pitching.Games);
        Assert.Equal(1, store.Official.Get(2)!.Pitching.Games);
    }

    [Fact]
    public void OfficialScope_Persists_AcrossTournaments()
    {
        var store = new PlayerStatStore();
        store.StartTournament();
        store.FoldGame(Box(), managerIsAway: false);
        store.StartTournament();

        Assert.Null(store.CurrentTournament.Get(1));
        Assert.Equal(2, store.Official.Get(1)!.Batting.Hits);
    }

    // ── テスト用の道具 ──

    private static IRandomSource Rng(ulong seed) => new Xoshiro256Random(seed);

    private static School School(double strength)
        => new() { Id = (int)strength, Name = "校", PrefectureId = 13, Strength = strength };

    /// <summary>常に同じ値を返す乱数源（受諾判定の境界テスト用）。</summary>
    private sealed class FixedRandom : IRandomSource
    {
        private readonly double _v;
        public FixedRandom(double v) => _v = v;
        public ulong NextUInt64() => (ulong)(_v * ulong.MaxValue);
        public double NextDouble() => _v;
        public int NextInt(int min, int max) => min;
        public double NextGaussian(double mean = 0, double stdDev = 1) => mean;
        public IRandomSource Fork(ulong streamId) => this;
    }

    /// <summary>詳細シムの継ぎ目のスタブ（乱数を1回消費して結果を作る＝決定論を検証できる）。</summary>
    private sealed class StubResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng)
            => new(Box(rng.NextInt(0, 10)), ManagerIsAway: false);

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng)
            => throw new NotSupportedException("練習試合はライブ観戦をまだ持たない。");
    }

    private static GameResult Box(int homeRuns = 5) => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 2, HomeRuns = homeRuns,
        InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
        HomeBatting = new[]
        {
            new BattingLine(3, FieldPosition.CenterField, "自1", 5, 4, 2, 1, 0, 1, 3, 1, 1, SourceId: 1),
        },
        HomePitching = new[]
        {
            new PitchingLine("先発", 27, 33, 5, 2, 7, 2, 120, SourceId: 2),
        },
    };
}

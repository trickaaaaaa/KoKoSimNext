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
using KokoSim.Engine.Tests.Match.Game;   // DeterminismCards / GameResultDigest（実エンジンで解く継ぎ目のテスト用）
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

    // ── ライブ観戦経路（issue #269） ──

    /// <summary>
    /// 観戦しても一括消化しても同じ試合になる契約（issue #269）。同じシードなら、ライブ経路を最後まで
    /// 進めたボックススコアが <see cref="PracticeMatchScheduler.Request"/> の <c>Detail.Result</c> と一致する。
    /// </summary>
    [Fact]
    public void BeginRequest_LiveBoxScore_MatchesRequestBoxScore_ForSameSeed()
    {
        GameResult RunBatch()
        {
            var s = new PracticeMatchScheduler(C);
            var m = new Manager { Fame = 100, Funds = 10 };
            return s.Request(m, School(55), School(58), week: 7, new EngineResolver(), Rng(4242)).Detail!.Result;
        }

        GameResult RunLive()
        {
            var s = new PracticeMatchScheduler(C);
            var m = new Manager { Fame = 100, Funds = 10 };
            var live = s.BeginRequest(m, School(55), School(58), week: 7, new EngineResolver(), Rng(4242));
            Assert.True(live.Started);
            return live.Live!.Progression.FinishRemaining();
        }

        var batch = RunBatch();
        var liveResult = RunLive();

        Assert.Equal(batch.AwayRuns, liveResult.AwayRuns);
        Assert.Equal(batch.HomeRuns, liveResult.HomeRuns);
        Assert.Equal(batch.InningsPlayed, liveResult.InningsPlayed);
        Assert.Equal(batch.TotalPitches, liveResult.TotalPitches);
        Assert.Equal(GameResultDigest.Sha256Of(batch), GameResultDigest.Sha256Of(liveResult));
    }

    /// <summary>ライブ経路でも費用と週枠は「開始時に1回だけ」確定する（申込成立＝費用発生）。</summary>
    [Fact]
    public void BeginRequest_ConsumesWeeklySlotAndFunds_ThenBlocksSameWeek()
    {
        var s = new PracticeMatchScheduler(C);
        var manager = new Manager { Fame = 100, Funds = 10 };

        var first = s.BeginRequest(manager, School(60), School(40), week: 3, new EngineResolver(), Rng(1));
        Assert.True(first.Started);
        Assert.NotNull(first.Live);
        Assert.Equal(9, manager.Funds, 6);
        Assert.Equal(3, s.LastPlayedWeek);

        // 同じ週の2回目は週枠で弾かれ、費用も進行体も発生しない。
        var second = s.BeginRequest(manager, School(60), School(40), week: 3, new EngineResolver(), Rng(2));
        Assert.False(second.Started);
        Assert.Equal(PracticeMatchRejection.AlreadyPlayedThisWeek, second.Rejection);
        Assert.Null(second.Live);
        Assert.Equal(9, manager.Funds, 6);

        // 週が変われば再び開始できる。
        Assert.True(s.BeginRequest(manager, School(60), School(40), week: 4, new EngineResolver(), Rng(1)).Started);
    }

    /// <summary>断られた場合・資金不足では、ライブ経路でも費用も週枠も消費しない（既存契約の維持）。</summary>
    [Fact]
    public void BeginRequest_DeclinedOrBroke_ConsumesNothing()
    {
        var s = new PracticeMatchScheduler(C);
        var manager = new Manager { Fame = 0, Funds = 10 };
        // 自校G・相手S＝受諾確率は下限0.02。乱数0.9固定なら必ず断られる。
        var declined = s.BeginRequest(manager, School(20), School(95), week: 1, new EngineResolver(), new FixedRandom(0.9));
        Assert.False(declined.Started);
        Assert.Equal(PracticeMatchRejection.Declined, declined.Rejection);
        Assert.Null(declined.Live);
        Assert.Equal(10, manager.Funds, 6);
        Assert.Null(s.LastPlayedWeek);

        var broke = new Manager { Fame = 100, Funds = 0.5 };
        var poor = s.BeginRequest(broke, School(60), School(40), week: 1, new EngineResolver(), Rng(1));
        Assert.False(poor.Started);
        Assert.Equal(PracticeMatchRejection.InsufficientFunds, poor.Rejection);
        Assert.Equal(0.5, broke.Funds, 6);
        Assert.Null(s.LastPlayedWeek);
    }

    /// <summary>
    /// <see cref="PracticeMatchScheduler.TryAccept"/> は副作用なしで、同じ種なら
    /// <see cref="PracticeMatchScheduler.BeginRequest"/> と必ず同じ受諾結果になる
    /// （スタメン設定を挟むフローの前提＝申込時に判定→開始時に再消費して Fork を揃える）。
    /// </summary>
    [Fact]
    public void TryAccept_HasNoSideEffects_AndAgreesWithBeginRequest()
    {
        var s = new PracticeMatchScheduler(C);
        var manager = new Manager { Fame = 100, Funds = 10 };

        var check = s.TryAccept(manager, School(60), School(40), week: 3, Rng(1));
        Assert.True(check.Accepted);
        Assert.Equal(10, manager.Funds, 6);    // 副作用なし
        Assert.Null(s.LastPlayedWeek);

        // 何度呼んでも同じ（副作用が無いので状態が動かない）。
        Assert.True(s.TryAccept(manager, School(60), School(40), week: 3, Rng(1)).Accepted);

        // 同じ種で作り直した rng を渡した BeginRequest と結論が一致する。
        var started = s.BeginRequest(manager, School(60), School(40), week: 3, new EngineResolver(), Rng(1));
        Assert.True(started.Started);
        Assert.Equal(check.AcceptChance, started.AcceptChance, 6);

        // 断られるケースでも一致する。
        var s2 = new PracticeMatchScheduler(C);
        var m2 = new Manager { Fame = 0, Funds = 10 };
        Assert.False(s2.TryAccept(m2, School(20), School(95), week: 1, new FixedRandom(0.9)).Accepted);
        Assert.False(s2.BeginRequest(m2, School(20), School(95), week: 1, new EngineResolver(), new FixedRandom(0.9)).Started);
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
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            => new(Box(rng.NextInt(0, 10)), ManagerIsAway: false);

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            => throw new NotSupportedException("このスタブは一括消化だけを見る（ライブは EngineResolver）。");
    }

    /// <summary>
    /// 実エンジンで解く継ぎ目（issue #269 のライブ／一括一致テスト用）。Shell の <c>PlayerMatchResolver</c> と
    /// 同じ契約＝Resolve と BeginLive が<b>同一チーム・同一 Fork</b>を使う（唯一の差は CaptureTimelines）。
    /// </summary>
    private sealed class EngineResolver : IPlayerMatchResolver
    {
        private static (Team Away, Team Home) Teams()
            => (DeterminismCards.Team("相手"), DeterminismCards.Team("自校"));

        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            var (away, home) = Teams();
            var ctx = new GameContext { MercyRuleEnabled = mercyRuleEnabled };
            return new PlayerMatchDetail(GameEngine.Play(away, home, ctx, rng.Fork(2)), ManagerIsAway: false);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            var (away, home) = Teams();
            // CaptureTimelines は RNG 中立＝全打席進めた結果は Resolve と一致する。
            var ctx = new GameContext { CaptureTimelines = true, MercyRuleEnabled = mercyRuleEnabled };
            return new PlayerMatchLive(new MatchProgression(away, home, ctx, rng.Fork(2)), ManagerIsAway: false);
        }
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

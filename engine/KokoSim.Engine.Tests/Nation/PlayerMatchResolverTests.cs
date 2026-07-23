using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 自校戦の詳細シム注入（IPlayerMatchResolver）の検証。核心は Fork 隔離＝詳細シムが本流乱数を消費せず、
/// 背景試合（裏4000校相当）の決着が resolver の内部消費量に依らず不変であること（決定論・不変条件#2）。
/// </summary>
public sealed class PlayerMatchResolverTests
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

    /// <summary>固定スコアを返すスタブ。forkDraws だけ隔離ストリームを消費（Fork隔離の検証用）。</summary>
    private sealed class StubResolver : IPlayerMatchResolver
    {
        private readonly int _mgrRuns, _oppRuns, _forkDraws;
        public StubResolver(int mgrRuns, int oppRuns, int forkDraws)
        { _mgrRuns = mgrRuns; _oppRuns = oppRuns; _forkDraws = forkDraws; }

        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            for (var i = 0; i < _forkDraws; i++) rng.NextDouble(); // 隔離ストリームを消費（本流に漏れなければ影響ゼロ）
            var result = new GameResult
            {
                AwayName = opponent.Name, HomeName = manager.Name,
                AwayRuns = _oppRuns, HomeRuns = _mgrRuns,
                InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            };
            return new PlayerMatchDetail(result, ManagerIsAway: false);
        }

        // このスタブは自動消化パスの検証専用（固定スコア）。ライブ進行は別テスト（LiveConsistentResolver）で検証する。
        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
            => throw new System.NotSupportedException("StubResolver は自動消化のみ（ライブ非対応）。");
    }

    private static TournamentRunner Runner(List<School> field, IPlayerMatchResolver? resolver, ulong seed = 7)
        => new(field, field[0], Coeff, new Xoshiro256Random(seed), Schedule, "テスト大会", resolver);

    private static List<string> BackgroundKeys(TournamentRunner r) =>
        r.BuildBracketView().Matches
            .Where(m => !m.ManagerInvolved)
            .Select(m => $"{m.RoundsRemaining}:{m.WinnerName} {m.WinnerScore}-{m.LoserScore} {m.LoserName}")
            .ToList();

    [Fact]
    public void ForkIsolation_ResolverDrawCount_DoesNotPerturbBackground()
    {
        // 同一シード・同一固定結果(自校5-0)で、resolver の内部乱数消費量だけを変える。
        var a = Runner(Field(60, 15, 55), new StubResolver(5, 0, forkDraws: 0));
        var b = Runner(Field(60, 15, 55), new StubResolver(5, 0, forkDraws: 9999));
        while (!a.Finished) a.PlayNextPlayerMatch();
        while (!b.Finished) b.PlayNextPlayerMatch();

        // 背景試合の全結果と優勝校が完全一致＝詳細シムの乱数が本流へ漏れていない。
        Assert.Equal(BackgroundKeys(a), BackgroundKeys(b));
        Assert.Equal(a.ChampionName, b.ChampionName);
    }

    [Fact]
    public void Determinism_SameSeedSameResolver_SameResult()
    {
        var a = Runner(Field(60, 15, 55), new StubResolver(3, 2, forkDraws: 5));
        var b = Runner(Field(60, 15, 55), new StubResolver(3, 2, forkDraws: 5));
        while (!a.Finished) a.PlayNextPlayerMatch();
        while (!b.Finished) b.PlayNextPlayerMatch();

        Assert.Equal(a.ChampionName, b.ChampionName);
        Assert.Equal(BackgroundKeys(a), BackgroundKeys(b));
    }

    [Fact]
    public void ManagerMatch_ExposesDetail_AndWinFromGameResult()
    {
        var r = Runner(Field(60, 15, 55), new StubResolver(7, 1, forkDraws: 3));
        var outcome = r.PlayNextPlayerMatch();

        Assert.NotNull(outcome.Detail);              // 成績集計の源
        Assert.False(outcome.ManagerWasAway);
        Assert.True(outcome.ManagerWon);
        Assert.Equal(7, outcome.ManagerScore);
        Assert.Equal(1, outcome.OpponentScore);
    }

    [Fact]
    public void ManagerLoss_FromGameResult_EndsTournamentForPlayer()
    {
        var r = Runner(Field(60, 15, 55), new StubResolver(1, 9, forkDraws: 0));
        var outcome = r.PlayNextPlayerMatch();

        Assert.False(outcome.ManagerWon);
        Assert.False(r.PlayerActive);
        Assert.NotNull(outcome.Detail);
        Assert.NotNull(r.BuildBracketView().ChampionName); // 背景消化で優勝校は確定
    }

    // ===== Issue #40: 相手校への敵AI采配注入後も Resolve/BeginLive の決定論契約が成立すること =====
    //
    // 実装本体（Assets/KokoSim/Shell/PlayerMatchResolver.BuildOpponentTeam）は Unity 側 asmdef にあり
    // xunit から直接参照できないため、同ファイルがしていること（StrengthTeamFactory.ForSchool の結果に
    // EnemyAiFactory.BrainFor を注入 → GameEngine.Play／MatchProgression を同一 teams・同一 rng.Fork(2) で
    // 呼ぶ）をここで再現する。ブレイン自体は rng を消費しないので、注入後も両者は完全一致するはず。

    private static Team OpponentTeamWithAiTactics(School opponent)
        => StrengthTeamFactory.ForSchool(opponent, yearIndex: 1) with { Tactics = EnemyAiFactory.BrainFor(opponent) };

    private static Team ManagerTeamStub()
        => StrengthTeamFactory.Create(58, "自校", new Xoshiro256Random(999));

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    public void OpponentTactics_Injected_ResolveAndBeginLive_ProduceSameBoxScore(ulong seed)
    {
        var opponent = Sch(200, 82); // A tier: 代打・伝令などフル運用可能な帯

        // Resolve 相当。
        var resolveRng = new Xoshiro256Random(seed);
        var resolveResult = GameEngine.Play(
            OpponentTeamWithAiTactics(opponent), ManagerTeamStub(), new GameContext(), resolveRng.Fork(2));

        // BeginLive 相当（唯一の差は CaptureTimelines=true）。
        var liveRng = new Xoshiro256Random(seed);
        var prog = new MatchProgression(
            OpponentTeamWithAiTactics(opponent), ManagerTeamStub(),
            new GameContext { CaptureTimelines = true }, liveRng.Fork(2));
        while (prog.Advance()) { /* 采配なしで最後まで進める */ }
        var liveResult = prog.BuildResult();

        Assert.Equal(resolveResult.AwayRuns, liveResult.AwayRuns);
        Assert.Equal(resolveResult.HomeRuns, liveResult.HomeRuns);
        Assert.Equal(resolveResult.TotalPitches, liveResult.TotalPitches);
        Assert.Equal(resolveResult.AwaySubstitutions, liveResult.AwaySubstitutions);
        Assert.Equal(resolveResult.HomeSubstitutions, liveResult.HomeSubstitutions);
    }
}

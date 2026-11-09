using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// ホーム画面の対戦カード・電光掲示板向け天気プレビュー（Issue #803）。
/// <see cref="ITournamentRunner.PeekNextMatchWeather"/> が、後で <c>IPlayerMatchResolver.Resolve</c> が
/// 実際に辿るのと同一のFork鎖（本流rng→対戦カードのFork→試合Fork→天候Fork）を辿ることを固定する＝
/// 「UIで抽選し直さない」契約の検証（覗いた値と実際に試合へ適用される値が常に一致する）。
/// </summary>
public sealed class PeekNextMatchWeatherTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new();
    private static readonly WeatherCoefficients Weather = new() { Enabled = true };

    private static School Sch(int id, double strength) => new() { Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength };

    private static List<School> Field(double managerStrength, int others, double othersStrength)
    {
        var list = new List<School> { Sch(1, managerStrength) };
        for (var i = 0; i < others; i++) list.Add(Sch(100 + i, othersStrength));
        return list;
    }

    private static Team MakeTeam(string name, int ability)
    {
        Player Pos(FieldPosition p) => new()
        {
            Position = p, Contact = ability, Power = ability, LaunchTendency = 50, Discipline = ability,
            Speed = ability, ArmStrength = ability, Fielding = ability, Catching = ability,
        };
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with
            {
                Name = name + "P",
                Pitching = new PitcherAttributes { MaxVelocityKmh = 130, Control = ability, StaminaPitches = 90, PitchRank = ability },
            },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    /// <summary>
    /// 実物の <see cref="KokoSim.Unity.Shell.PlayerMatchResolver"/> と同じ手順（rng.Fork(2) を天候＋本解決の
    /// 両方で使う）を踏む検証用リゾルバ。天候は <see cref="WeatherModel.SampleForMatch"/> で覗いてから、
    /// <b>同じ rng</b> で <see cref="GameEngine.Play"/> を呼ぶ（Fork は親状態を進めないので二重取得しても同じ値）。
    /// </summary>
    private sealed class CapturingResolver : IPlayerMatchResolver
    {
        public WeatherSample? Captured;

        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null)
        {
            Captured = WeatherModel.SampleForMatch(rng.Fork(2), Weather);
            var ctx = new GameContext { MercyRuleEnabled = mercyRuleEnabled, Weather = Weather };
            var result = GameEngine.Play(MakeTeam(manager.Name, 60), MakeTeam(opponent.Name, 55), ctx, rng.Fork(2));
            return new(result, ManagerIsAway: true);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled,
            TournamentMatchContext? context = null) => throw new System.NotSupportedException();
    }

    /// <summary>
    /// 裏カードを固定スコアで即答するだけのダミー（フルシム相当）。渡すことで
    /// <c>TournamentRunner.ConsumeLegacyManagerCardRng</c> の集計モデル向けダミー消費
    /// （<c>_backgroundResolver is null</c> のときだけ走る旧経路）を回避し、本流 <c>_rng</c> を
    /// Fork 以外で一切消費しない実機構成（フルシム＝裏試合もFork）に揺らす。
    /// </summary>
    private sealed class FixedScoreBackgroundResolver : IBackgroundMatchResolver
    {
        public GameResult Resolve(School away, School home, IRandomSource rng, TournamentMatchContext? context = null)
            => new()
            {
                AwayName = away.Name, HomeName = home.Name,
                AwayRuns = 3, HomeRuns = 1, AwayHits = 6, HomeHits = 4,
                InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            };
    }

    [Fact]
    public void TournamentRunner_PeekMatchesWhatResolveActuallyUses()
    {
        var resolver = new CapturingResolver();
        var r = new TournamentRunner(Field(60, 7, 55), Sch(1, 60), Coeff,
            new Xoshiro256Random(42), Schedule, "テスト大会", resolver, new FixedScoreBackgroundResolver());

        var peeked = r.PeekNextMatchWeather(Weather);
        Assert.NotNull(peeked);

        r.PlayNextPlayerMatch();
        Assert.Equal(peeked, resolver.Captured);
    }

    [Fact]
    public void TournamentRunner_PeekingRepeatedly_IsIdempotentAndDoesNotAdvanceRng()
    {
        var resolver = new CapturingResolver();
        var r = new TournamentRunner(Field(60, 7, 55), Sch(1, 60), Coeff,
            new Xoshiro256Random(42), Schedule, "テスト大会", resolver, new FixedScoreBackgroundResolver());

        var first = r.PeekNextMatchWeather(Weather);
        var second = r.PeekNextMatchWeather(Weather);
        Assert.Equal(first, second);

        r.PlayNextPlayerMatch();
        Assert.Equal(first, resolver.Captured);
    }

    [Fact]
    public void TournamentRunner_Finished_PeekReturnsNull()
    {
        var resolver = new CapturingResolver();
        var r = new TournamentRunner(Field(1, 0, 0), Sch(1, 1), Coeff,
            new Xoshiro256Random(42), Schedule, "テスト大会", resolver, new FixedScoreBackgroundResolver());
        // entrants=1(自校のみ)＝開幕時点で優勝が確定し次戦が無い。
        Assert.True(r.Finished);
        Assert.Null(r.PeekNextMatchWeather(Weather));
    }

    [Fact]
    public void BlockLeagueRunner_PeekMatchesWhatResolveActuallyUses()
    {
        var resolver = new CapturingResolver();
        var schools = Field(60, 5, 50);
        var blocks = new List<IReadOnlyList<School>> { schools };
        var r = new BlockLeagueRunner(blocks, schools[0], 2, Coeff,
            new Xoshiro256Random(11), Schedule, "地区予選", resolver, new FixedScoreBackgroundResolver());

        var peeked = r.PeekNextMatchWeather(Weather);
        Assert.NotNull(peeked);

        r.PlayNextPlayerMatch();
        Assert.Equal(peeked, resolver.Captured);
    }
}

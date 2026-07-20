using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// A-6: 球数制限の実適用（設計書05 §1.3, 現代ルール）。ModernRules.EffectiveWeeklyPitchLimit を
/// GameContext.WeeklyPitchLimit に流し込み、週の持ち越し球数＋当試合の球数が上限に達した投手を継投へ回す。
/// 既定（上限なし）では従来の継投挙動・統計帯を一切変えないことを合わせて検証する。
/// </summary>
public sealed class PitchLimitTests
{
    private static (Team Away, Team Home, Player HomeStarter) Teams(ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var away = StrengthTeamFactory.Create(52, "A", rng);
        var home = StrengthTeamFactory.Create(52, "B", rng);
        return (away, home, home.BattingOrder[home.PitcherSlot]);
    }

    [Fact]
    public void PitchLimit_WithHighWeeklyCarry_ForcesEarlyRelief()
    {
        var (away, home, starter) = Teams(5);
        // 先発は今週すでに480球を投げている。上限500 → 当試合は20球前後で継投へ。
        var prior = new Dictionary<Player, int> { [starter] = 480 };
        var limited = GameEngine.Play(away, home, new GameContext { WeeklyPitchLimit = 500 }, new Xoshiro256Random(9), prior);
        var free = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(9));

        // 先発は上限で早々に降板（残り20球ぶんだけ）＝ 上限なしの完投級より大幅に少ない。
        Assert.True(limited.HomePitching[0].Pitches < 45,
            $"上限で降板した先発の球数が多すぎる: {limited.HomePitching[0].Pitches}");
        Assert.True(free.HomePitching[0].Pitches > limited.HomePitching[0].Pitches + 30,
            $"上限なしの先発({free.HomePitching[0].Pitches})が上限あり({limited.HomePitching[0].Pitches})より十分多くない");
        Assert.True(limited.HomePitching.Count >= 2, "継投が発生していない（先発のみ）");
    }

    [Fact]
    public void NoLimit_PriorPitchesHaveNoEffect_BandUnchanged()
    {
        // 上限なし（既定 int.MaxValue）なら、持ち越し球数を渡しても試合は1ビットも変わらない＝帯保護。
        var (away, home, starter) = Teams(5);
        var prior = new Dictionary<Player, int> { [starter] = 9_999 };
        var baseline = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(9));
        var withCarry = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(9), prior);

        Assert.Equal(baseline.AwayRuns, withCarry.AwayRuns);
        Assert.Equal(baseline.HomeRuns, withCarry.HomeRuns);
        Assert.Equal(baseline.TotalPitches, withCarry.TotalPitches);
        Assert.Equal(baseline.PitcherChanges, withCarry.PitcherChanges);
        Assert.Equal(baseline.HomePitching[0].Pitches, withCarry.HomePitching[0].Pitches);
    }

    [Fact]
    public void PitchLimit_IsDeterministic()
    {
        var (away, home, starter) = Teams(5);
        var prior = new Dictionary<Player, int> { [starter] = 470 };
        var ctx = new GameContext { WeeklyPitchLimit = 500 };
        var a = GameEngine.Play(away, home, ctx, new Xoshiro256Random(3), prior);
        var b = GameEngine.Play(away, home, ctx, new Xoshiro256Random(3), prior);
        Assert.Equal(a.HomeRuns, b.HomeRuns);
        Assert.Equal(a.AwayRuns, b.AwayRuns);
        Assert.Equal(a.HomePitching[0].Pitches, b.HomePitching[0].Pitches);
        Assert.Equal(a.PitcherChanges, b.PitcherChanges);
    }

    [Fact]
    public void ModernRules_FeedsWeeklyPitchLimit_ByYear()
    {
        var rules = new ModernRules();
        // 導入年（2020）以降は500、それ以前は上限なし（int.MaxValue）。
        Assert.Equal(500, new GameContext { WeeklyPitchLimit = rules.EffectiveWeeklyPitchLimit(2021) }.WeeklyPitchLimit);
        Assert.Equal(int.MaxValue, new GameContext { WeeklyPitchLimit = rules.EffectiveWeeklyPitchLimit(2019) }.WeeklyPitchLimit);
    }
}

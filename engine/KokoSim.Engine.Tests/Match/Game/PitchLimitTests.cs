using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
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
        var limited = GameEngine.Play(away, home, new GameContext { WeeklyPitchLimit = 500 }, new Xoshiro256Random(9),
            homePriorWeekPitches: prior);
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
        var withCarry = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(9), homePriorWeekPitches: prior);

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
        var a = GameEngine.Play(away, home, ctx, new Xoshiro256Random(3), homePriorWeekPitches: prior);
        var b = GameEngine.Play(away, home, ctx, new Xoshiro256Random(3), homePriorWeekPitches: prior);
        Assert.Equal(a.HomeRuns, b.HomeRuns);
        Assert.Equal(a.AwayRuns, b.AwayRuns);
        Assert.Equal(a.HomePitching[0].Pitches, b.HomePitching[0].Pitches);
        Assert.Equal(a.PitcherChanges, b.PitcherChanges);
    }

    /// <summary>
    /// 持ち越し球数の辞書は<b>チームごとに別</b>（issue: 球数制限の可視化）。相手チームの辞書へ入れた
    /// 持ち越しが自チームの投手に効いてはいけない（Player は値等価の record なので、1本に merge すると
    /// 別校の選手が全属性一致したとき潰れる＝dayForm / PitchRecovery と同じ理由で2本に割った）。
    /// </summary>
    [Fact]
    public void PriorWeekPitches_AreScopedToTheirOwnTeam()
    {
        var (away, home, starter) = Teams(5);
        var prior = new Dictionary<Player, int> { [starter] = 480 };
        var ctx = new GameContext { WeeklyPitchLimit = 500 };

        // home の先発の持ち越しを **away 側の辞書** に入れても、home の継投は一切変わらない。
        var misplaced = GameEngine.Play(away, home, ctx, new Xoshiro256Random(9), awayPriorWeekPitches: prior);
        var baseline = GameEngine.Play(away, home, ctx, new Xoshiro256Random(9));
        Assert.Equal(baseline.HomePitching[0].Pitches, misplaced.HomePitching[0].Pitches);
        Assert.Equal(baseline.PitcherChanges, misplaced.PitcherChanges);

        // 正しい側（home）へ入れたときだけ効く。
        var correct = GameEngine.Play(away, home, ctx, new Xoshiro256Random(9), homePriorWeekPitches: prior);
        Assert.True(correct.HomePitching[0].Pitches < baseline.HomePitching[0].Pitches,
            $"home 側の持ち越しが効いていない: {correct.HomePitching[0].Pitches} vs {baseline.HomePitching[0].Pitches}");
    }

    /// <summary>
    /// HUD が「87 / 118球」「週残N球」を出すための観測値（issue: 球数制限の可視化）。
    /// 持ち越し・上限・実効目安投球数が現投手の LivePitcherToday に載ること。
    /// </summary>
    [Fact]
    public void LivePitcherToday_ExposesWeeklyBudgetAndStaminaTarget()
    {
        var (away, home, starter) = Teams(5);
        var prior = new Dictionary<Player, int> { [starter] = 300 };
        var ctx = new GameContext { WeeklyPitchLimit = 500 };
        var p = GameEngine.NewProgress(away, home, ctx, new Xoshiro256Random(9), homePriorWeekPitches: prior);

        var line = p.Home.LivePitcherLine();
        Assert.Equal(300, line.WeeklyPitchesPrior);
        Assert.Equal(500, line.WeeklyPitchLimit);
        // 分母はスタミナバーの分母（実効目安投球数）そのもの＝素の目安投球数と一致（回復モデル未注入）。
        Assert.Equal(starter.Pitching.StaminaPitches, line.StaminaTarget, 6);

        // 上限なし（既定）なら int.MaxValue＝UI は残り球数を出さない。
        var free = GameEngine.NewProgress(away, home, new GameContext(), new Xoshiro256Random(9));
        Assert.Equal(int.MaxValue, free.Home.LivePitcherLine().WeeklyPitchLimit);
        Assert.Equal(0, free.Home.LivePitcherLine().WeeklyPitchesPrior);
    }

    /// <summary>
    /// 相手校（AI采配つき）の投手も球数制限を受ける。自校は engine 直判定／相手校は Brain 経由と
    /// 経路が分かれる（<see cref="GameEngine.MaybeChangePitcher"/>）ので、AI 側も
    /// <see cref="PitchingChangeSituation.AtWeeklyLimit"/> を尊重することを固定する
    /// （<see cref="StandardTacticsBrain.CallPitchingChange"/> が無条件に返し、AiTacticsBrain は
    /// それを人選だけ載せ替えて通す）。
    /// </summary>
    [Fact]
    public void PitchLimit_AppliesToOpponentWithAiBrain()
    {
        var (awayRaw, homeRaw, homeStarter) = Teams(5);
        Team WithAi(Team t) => t with
        {
            Tactics = new AiTacticsBrain(new AiProfile(90, 7, SchoolStyle.DefensiveMinded)),
        };
        var away = WithAi(awayRaw);
        var home = WithAi(homeRaw);
        var prior = new Dictionary<Player, int> { [homeStarter] = 480 };
        var ctx = new GameContext { WeeklyPitchLimit = 500 };

        var limited = GameEngine.Play(away, home, ctx, new Xoshiro256Random(9), homePriorWeekPitches: prior);
        var free = GameEngine.Play(away, home, ctx, new Xoshiro256Random(9));

        Assert.True(limited.HomePitching[0].Pitches < 45,
            $"AI采配の先発が上限で降りていない: {limited.HomePitching[0].Pitches}");
        Assert.True(free.HomePitching[0].Pitches > limited.HomePitching[0].Pitches + 30,
            $"持ち越しなし({free.HomePitching[0].Pitches})と差が出ていない({limited.HomePitching[0].Pitches})");
        Assert.True(limited.HomePitching.Count >= 2, "継投が発生していない（先発のみ）");
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

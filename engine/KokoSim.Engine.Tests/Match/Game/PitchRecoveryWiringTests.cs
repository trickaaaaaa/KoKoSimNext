using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 試合間の回復モデル本流配線（設計書02 §1.1e-2, issue #41）。<see cref="PitchRecoveryModel"/> 自体は
/// <c>PitchRecoveryTests</c> で検証済み。ここでは <see cref="GameEngine.Play"/> の pitchRecovery 引数が
/// 実際に継投判断・実効能力へ反映されること、既定（null）では従来挙動・統計帯を一切変えないことを固定する。
/// PitchLimitTests（球数制限, 設計書05 §1.3）と同型の検証形。
/// </summary>
public sealed class PitchRecoveryWiringTests
{
    private static (Team Away, Team Home, Player HomeStarter) Teams(ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var away = StrengthTeamFactory.Create(52, "A", rng);
        var home = StrengthTeamFactory.Create(52, "B", rng);
        return (away, home, home.BattingOrder[home.PitcherSlot]);
    }

    [Fact]
    public void PitchRecovery_WithLowEffectiveStamina_ForcesEarlyRelief()
    {
        var (away, home, starter) = Teams(5);
        // 連戦で実効目安が30球まで落ちた想定（素の目安は100前後）。マージン25を足しても55球前後で継投。
        var recovery = new Dictionary<Player, double> { [starter] = 30.0 };
        var degraded = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(9), homePitchRecovery: recovery);
        var fresh = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(9));

        Assert.True(degraded.HomePitching[0].Pitches < 70,
            $"実効目安30で降板した先発の球数が多すぎる: {degraded.HomePitching[0].Pitches}");
        Assert.True(fresh.HomePitching[0].Pitches > degraded.HomePitching[0].Pitches + 20,
            $"回復劣化なし({fresh.HomePitching[0].Pitches})が劣化あり({degraded.HomePitching[0].Pitches})より十分多くない");
        Assert.True(degraded.HomePitching.Count >= 2, "継投が発生していない（先発のみ）");
    }

    [Fact]
    public void NoPitchRecovery_BandUnchanged()
    {
        // 既定（pitchRecovery=null）なら試合は1ビットも変わらない＝帯保護（Balance CLI 等は常にこちら）。
        var (away, home, _) = Teams(5);
        var baseline = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(9));
        var withNullRecovery = GameEngine.Play(
            away, home, new GameContext(), new Xoshiro256Random(9), priorWeekPitches: null,
            awayPitchRecovery: null, homePitchRecovery: null);

        Assert.Equal(baseline.AwayRuns, withNullRecovery.AwayRuns);
        Assert.Equal(baseline.HomeRuns, withNullRecovery.HomeRuns);
        Assert.Equal(baseline.TotalPitches, withNullRecovery.TotalPitches);
        Assert.Equal(baseline.PitcherChanges, withNullRecovery.PitcherChanges);
        Assert.Equal(baseline.HomePitching[0].Pitches, withNullRecovery.HomePitching[0].Pitches);
    }

    [Fact]
    public void EmptyPitchRecoveryDictionary_BehavesSameAsNull()
    {
        // 空辞書（対象投手が誰も登録されていない）も従来挙動と一致する＝素の目安投球数へフォールバック。
        var (away, home, _) = Teams(5);
        var baseline = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(9));
        var withEmpty = GameEngine.Play(
            away, home, new GameContext(), new Xoshiro256Random(9),
            homePitchRecovery: new Dictionary<Player, double>());

        Assert.Equal(baseline.HomePitching[0].Pitches, withEmpty.HomePitching[0].Pitches);
        Assert.Equal(baseline.PitcherChanges, withEmpty.PitcherChanges);
    }

    [Fact]
    public void PitchRecovery_IsDeterministic()
    {
        var (away, home, starter) = Teams(5);
        var recovery = new Dictionary<Player, double> { [starter] = 40.0 };
        var a = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(3), homePitchRecovery: recovery);
        var b = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(3), homePitchRecovery: recovery);
        Assert.Equal(a.HomeRuns, b.HomeRuns);
        Assert.Equal(a.AwayRuns, b.AwayRuns);
        Assert.Equal(a.HomePitching[0].Pitches, b.HomePitching[0].Pitches);
        Assert.Equal(a.PitcherChanges, b.PitcherChanges);
    }

    [Fact]
    public void TeamState_EffectiveBaseStamina_FallsBackToRaw_WhenNoOverrideRegistered()
    {
        var (away, home, starter) = Teams(11);
        var state = new TeamState(home);
        Assert.Equal(starter.Pitching.StaminaPitches, state.EffectiveBaseStamina(starter));

        state.SetPitchRecovery(new Dictionary<Player, double> { [starter] = 42.0 });
        Assert.Equal(42.0, state.EffectiveBaseStamina(starter));

        // 別の投手（辞書未登録）は素の値のまま。
        var reliever = home.Reserves[0];
        Assert.Equal(reliever.Pitching.StaminaPitches, state.EffectiveBaseStamina(reliever));

        state.SetPitchRecovery(null);
        Assert.Equal(starter.Pitching.StaminaPitches, state.EffectiveBaseStamina(starter));
    }
}

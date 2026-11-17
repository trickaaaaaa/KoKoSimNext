using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 気温による実効目安球数の縮小・本流配線（issue #1071）。<see cref="WeatherModel.StaminaPitchFactor"/> 自体の
/// 式は <c>WeatherModelTests</c> で検証済み。ここでは <see cref="GameEngine.NewProgress"/> が実際にその倍率を
/// <see cref="TeamState.EffectiveBaseStamina"/> へ反映すること、基準温以下・屋内・Weather無効では既定1.0のまま
/// （従来挙動と完全一致）であることを固定する。<c>PitchRecoveryWiringTests</c> と同型の検証形。
/// </summary>
public sealed class WeatherFatigueWiringTests
{
    // 分散をゼロにして気温を1点に固定する（Min=Max=Mean）。IndoorMatch_IsIdenticalToAMatchWithoutWeather と同じ手法。
    private static WeatherCoefficients FixedTemperature(double c) => new() { MeanTemperatureC = c, MinTemperatureC = c, MaxTemperatureC = c };

    private static (Team Away, Team Home, Player HomeStarter) Teams(ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var away = StrengthTeamFactory.Create(52, "A", rng);
        var home = StrengthTeamFactory.Create(52, "B", rng);
        return (away, home, home.BattingOrder[home.PitcherSlot]);
    }

    [Fact]
    public void TeamState_EffectiveBaseStamina_AppliesWeatherFactor_CombinedWithPitchRecovery()
    {
        var (_, home, starter) = Teams(11);
        var state = new TeamState(home);
        var baseStamina = starter.Pitching.StaminaPitches;

        state.SetWeatherStaminaFactor(0.9);
        Assert.Equal(baseStamina * 0.9, state.EffectiveBaseStamina(starter), 9);

        // 試合間の回復モデル（PitchRecovery）と乗算で重なる＝連戦の消耗＋暑さの両方が効く。
        state.SetPitchRecovery(new Dictionary<Player, double> { [starter] = 50.0 });
        Assert.Equal(45.0, state.EffectiveBaseStamina(starter), 9);

        state.SetWeatherStaminaFactor(1.0);
        Assert.Equal(50.0, state.EffectiveBaseStamina(starter), 9);
    }

    [Fact]
    public void NewProgress_HotWeather_Reduces36DegreeStaminaByTenPercent()
    {
        var (away, home, starter) = Teams(5);
        var baseStamina = starter.Pitching.StaminaPitches;
        var ctx = new GameContext { Weather = FixedTemperature(36.0) };

        var p = GameEngine.NewProgress(away, home, ctx, new Xoshiro256Random(1));

        Assert.Equal(baseStamina * 0.90, p.Home.EffectiveBaseStamina(starter), 6);
    }

    [Fact]
    public void NewProgress_AtOrBelowBaselineTemperature_LeavesStaminaUnchanged()
    {
        var (away, home, starter) = Teams(5);
        var baseStamina = starter.Pitching.StaminaPitches;
        var ctx = new GameContext { Weather = FixedTemperature(25.0) }; // 基準30℃未満

        var p = GameEngine.NewProgress(away, home, ctx, new Xoshiro256Random(1));

        Assert.Equal(baseStamina, p.Home.EffectiveBaseStamina(starter), 9);
    }

    [Fact]
    public void NewProgress_IndoorStadium_IgnoresHotWeather()
    {
        var (away, home, starter) = Teams(5);
        var baseStamina = starter.Pitching.StaminaPitches;
        var dome = KokoSim.Engine.Match.Field.Stadium.AutumnFinals[0].ToFieldGeometry();
        Assert.True(dome.Indoor);
        var ctx = new GameContext { Weather = FixedTemperature(36.0), Field = dome };

        var p = GameEngine.NewProgress(away, home, ctx, new Xoshiro256Random(1));

        Assert.Equal(baseStamina, p.Home.EffectiveBaseStamina(starter), 9);
    }

    [Fact]
    public void NewProgress_WeatherNull_LeavesStaminaUnchanged()
    {
        var (away, home, starter) = Teams(5);
        var baseStamina = starter.Pitching.StaminaPitches;

        var p = GameEngine.NewProgress(away, home, new GameContext(), new Xoshiro256Random(1));

        Assert.Equal(baseStamina, p.Home.EffectiveBaseStamina(starter), 9);
    }

    [Fact]
    public void HotWeather_TriggersReliefEarlier_AtTheSamePitchCount()
    {
        // 「暑いと継投が早まる」（issue #1071 のやること）を ShouldRelieve で直接固定する。
        // 同じ実投球数でも、縮んだ実効目安（暑い方）だけがマージンを超えて継投判定になる。
        var (away, home, starter) = Teams(5);
        var fatigue = new FatigueCoefficients();
        var baseStamina = starter.Pitching.StaminaPitches;

        var hotCtx = GameEngine.NewProgress(away, home, new GameContext { Weather = FixedTemperature(36.0) }, new Xoshiro256Random(1));
        var coolCtx = GameEngine.NewProgress(away, home, new GameContext { Weather = FixedTemperature(25.0) }, new Xoshiro256Random(1));
        var hotStamina = hotCtx.Home.EffectiveBaseStamina(starter);
        var coolStamina = coolCtx.Home.EffectiveBaseStamina(starter);
        Assert.True(hotStamina < coolStamina, "36℃の実効目安が25℃以上になっている");
        Assert.Equal(baseStamina, coolStamina, 9);

        // 両者のちょうど中間の球数: 暑い方は継投判断、涼しい方はまだ継投判断にならない。
        var pitchesThrown = (hotStamina + fatigue.RelievePitchMargin + coolStamina + fatigue.RelievePitchMargin) / 2.0;

        Assert.True(PitchingFatigue.ShouldRelieve(starter, pitchesThrown, fatigue, hotStamina));
        Assert.False(PitchingFatigue.ShouldRelieve(starter, pitchesThrown, fatigue, coolStamina));
    }
}

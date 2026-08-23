using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 消耗カーブ（issue fatigue-gradual-before-threshold・設計書02 §1.1e）: 目安の手前から単調に低下し、
/// 目安到達時の低下量が係数どおりで、超過後は段差なく続く。
/// </summary>
public sealed class FatigueCurveTests
{
    private static readonly FatigueCoefficients C = new();
    private const double Stamina = 123.0;   // スタミナLv80 相当（63 + 0.75×80）

    [Fact]
    public void BeforeOnset_NoDrop()
    {
        var (v, c) = PitchingFatigue.Drops(Stamina * C.OnsetRatio - 1, Stamina, C);
        Assert.Equal(0.0, v);
        Assert.Equal(0.0, c);
    }

    [Fact]
    public void BetweenOnsetAndGuideline_MonotonicallyIncreasing()
    {
        var prevV = 0.0; var prevC = 0.0;
        for (var p = Stamina * C.OnsetRatio; p <= Stamina; p += 1.0)
        {
            var (v, c) = PitchingFatigue.Drops(p, Stamina, C);
            Assert.True(v >= prevV && c >= prevC, $"{p}球で低下が減った");
            prevV = v; prevC = c;
        }
        // 目安の手前でも実際に効いている（階段型ではない）。
        var (vMid, _) = PitchingFatigue.Drops(Stamina * 0.9, Stamina, C);
        Assert.True(vMid > 0.5, $"目安の90%で球速低下 {vMid:F2}km/h（効いていない）");
    }

    [Fact]
    public void AtGuideline_DropEqualsCoefficient_AndContinuesWithoutStep()
    {
        var (v, c) = PitchingFatigue.Drops(Stamina, Stamina, C);
        Assert.Equal(C.VelocityDropAtGuidelineKmh, v, 9);
        Assert.Equal(C.ControlDropAtGuideline, c, 9);

        var (v1, c1) = PitchingFatigue.Drops(Stamina + 1, Stamina, C);
        Assert.Equal(C.VelocityDropAtGuidelineKmh + C.VelocityDropPerOverPitch, v1, 9);
        Assert.Equal(C.ControlDropAtGuideline + C.ControlDropPerOverPitch, c1, 9);
        // 目安の直前→直後で段差が無い（1球ぶんの傾き以内）。
        var (v0, _) = PitchingFatigue.Drops(Stamina - 1, Stamina, C);
        Assert.True(v - v0 < 0.2, $"目安の前後で段差 {v - v0:F3}");
    }

    [Fact]
    public void Effective_AppliesTheCurve_ToVelocityCeilingAndControl()
    {
        var pitcher = new Player
        {
            Position = KokoSim.Engine.Match.Field.FieldPosition.Pitcher,
            Pitching = PitcherAttributes.LeagueAverage with { MaxVelocityKmh = 146.0, Control = 80, StaminaPitches = Stamina },
        };
        var fresh = PitchingFatigue.Effective(pitcher, 10, C);
        var at112 = PitchingFatigue.Effective(pitcher, 112, C);
        var atGuideline = PitchingFatigue.Effective(pitcher, Stamina, C);
        Assert.Equal(146.0, fresh.MaxVelocityKmh);
        Assert.True(at112.MaxVelocityKmh < 146.0 && at112.MaxVelocityKmh > 144.0);   // 112球目は1球目と同じではない
        Assert.Equal(144.0, atGuideline.MaxVelocityKmh, 6);
        Assert.Equal(75, atGuideline.Control);
    }

    [Fact]
    public void Effective_WithOverride_UsesInjectedStamina_InsteadOfPlayerAttribute()
    {
        // 試合間の回復モデル（issue #41 本流配線）: 実効目安投球数を渡した版は基準の StaminaPitches を無視する。
        var pitcher = new Player
        {
            Position = KokoSim.Engine.Match.Field.FieldPosition.Pitcher,
            Pitching = PitcherAttributes.LeagueAverage with { MaxVelocityKmh = 146.0, Control = 80, StaminaPitches = Stamina },
        };
        // 選手本来の目安（Stamina=123）ならまだ効かない球数でも、回復劣化で実効30なら既に目安超過。
        var withoutOverride = PitchingFatigue.Effective(pitcher, 40, C);
        var withOverride = PitchingFatigue.Effective(pitcher, 40, C, effectiveStaminaPitches: 30.0);
        Assert.Equal(146.0, withoutOverride.MaxVelocityKmh);
        Assert.True(withOverride.MaxVelocityKmh < 146.0, $"実効30なのに劣化していない: {withOverride.MaxVelocityKmh}");

        // 4引数版に素の StaminaPitches をそのまま渡せば3引数版と一致する（後方互換）。
        var identity = PitchingFatigue.Effective(pitcher, 112, C, pitcher.Pitching.StaminaPitches);
        var legacy = PitchingFatigue.Effective(pitcher, 112, C);
        Assert.Equal(legacy.MaxVelocityKmh, identity.MaxVelocityKmh, 9);
        Assert.Equal(legacy.Control, identity.Control);
    }

    [Fact]
    public void ShouldRelieve_WithOverride_UsesInjectedStamina_InsteadOfPlayerAttribute()
    {
        var pitcher = new Player
        {
            Position = KokoSim.Engine.Match.Field.FieldPosition.Pitcher,
            Pitching = PitcherAttributes.LeagueAverage with { StaminaPitches = Stamina },
        };
        // 素の目安(123)+マージン(25)=148球まで継投不要。回復劣化で実効30なら 30+25=55球で継投。
        Assert.False(PitchingFatigue.ShouldRelieve(pitcher, 50, C));
        Assert.True(PitchingFatigue.ShouldRelieve(pitcher, 56, C, effectiveStaminaPitches: 30.0),
            "実効30+マージン25=55球を超えても継投トリガーが立っていない");
        Assert.False(PitchingFatigue.ShouldRelieve(pitcher, 54, C, effectiveStaminaPitches: 30.0),
            "55球未満でトリガーが立ってしまっている");

        // 素の StaminaPitches をそのまま渡せば3引数版と一致する（後方互換）。
        Assert.Equal(
            PitchingFatigue.ShouldRelieve(pitcher, 130, C),
            PitchingFatigue.ShouldRelieve(pitcher, 130, C, pitcher.Pitching.StaminaPitches));
    }

    [Fact]
    public void OnsetRatioOne_ReproducesTheOldStepShape()
    {
        var step = C with { OnsetRatio = 1.0, VelocityDropAtGuidelineKmh = 0.0, ControlDropAtGuideline = 0.0 };
        Assert.Equal((0.0, 0.0), PitchingFatigue.Drops(Stamina - 0.5, Stamina, step));
        var (v, c) = PitchingFatigue.Drops(Stamina + 10, Stamina, step);
        Assert.Equal(10 * step.VelocityDropPerOverPitch, v, 9);
        Assert.Equal(10 * step.ControlDropPerOverPitch, c, 9);
    }
}

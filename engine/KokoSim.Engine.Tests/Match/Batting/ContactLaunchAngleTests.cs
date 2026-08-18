using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>
/// ミートによる打球角度σの締まり（issue ai-roster-power-top-tier-all-sluggers, 2026-08-18）:
/// ミートが良いほどライナー帯（安打）に集まり、本塁打帯からは外れる＝「パワーは本塁打・ミートは安打」の分業。
/// </summary>
public sealed class ContactLaunchAngleTests
{
    private static (double avg, double hr) Run(BattingCoefficients bc, int contact, int power = 60, ulong seed = 7, int n = 6000)
    {
        var geom = new FieldGeometry();
        var ctx = new AtBatContext { Field = geom, Fielders = geom.StandardAlignment(), Batting = bc };
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 132, Control = 50, PitchRank = 50, StaminaPitches = 90, Repertoire = new[] { PitchSlot.FastballOf(50), new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 50 } } };
        var b = new BatterAttributes { Contact = contact, Power = power, LaunchTendency = 50, Discipline = 50, Speed = 50 };
        var rng = new Xoshiro256Random(seed); int h = 0, ab = 0, hr = 0;
        for (var i = 0; i < n; i++)
        {
            var r = AtBatResolver.ResolveDetailed(b, pitcher, ctx, rng).Result;
            if (r.IsHit()) h++; if (r.IsAtBat()) ab++; if (r == PlateAppearanceResult.HomeRun) hr++;
        }
        return ((double)h / ab, (double)hr / n);
    }

    [Fact]
    public void HighContact_HitsMore_AndHomersLess_ThanWithoutTheTightening()
    {
        var off = new BattingCoefficients { LaunchAngleSigmaPerContact = 0.0 };
        var on = new BattingCoefficients();   // 既定 0.010
        var (avgOff, hrOff) = Run(off, 90);
        var (avgOn, hrOn) = Run(on, 90);
        Assert.True(avgOn > avgOff + 0.010, $"C90 の打率が上がらない: {avgOff:F3}→{avgOn:F3}");
        Assert.True(hrOn < hrOff * 0.75, $"C90 の本塁打率が下がらない: {hrOff:P1}→{hrOn:P1}");
    }

    [Fact]
    public void AverageContact_IsUnchanged()
    {
        var off = new BattingCoefficients { LaunchAngleSigmaPerContact = 0.0 };
        var on = new BattingCoefficients();
        Assert.Equal(Run(off, 50), Run(on, 50));   // C50 では倍率 1.0＝同一乱数列で完全一致
    }

    [Fact]
    public void Tightening_IsFlooredAtTheConfiguredMinimum()
    {
        // C99 でも σ倍率は floor（0.6）で止まる＝C90 と同じ倍率（同一乱数列で角度が同じ）。
        var bc = new BattingCoefficients { LaunchAngleSigmaContactFloor = 0.6, LaunchAngleSigmaPerContact = 0.010 };
        var a = Run(bc, 90, seed: 3, n: 3000);
        var b = Run(bc, 99, seed: 3, n: 3000);
        // 空振り率はミートで変わるので完全一致は要求しない＝本塁打率が C90 より上がらないことだけを見る
        Assert.True(b.hr <= a.hr + 0.005, $"C99 の本塁打率 {b.hr:P1} > C90 {a.hr:P1}");
    }
}

using System.Globalization;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 肩（地肩）→送球速度の幅（issue #764「野手の送球がしょぼい」）。
/// 肩を守備と同じ効き 0.45 で寝かせていた頃は、肩100でも約126km/h・肩20との差は18km/hしかなかった。
/// 肩は送球速度にしか使われず、送球は km/h で語られる量なので、球速と同じく表示どおりに通す。
/// 実ゲームと同じ経路（<see cref="Player.ToFielder(AbilityEffectCoefficients)"/>＋C#既定の効き）で測る。
/// </summary>
public sealed class ArmThrowSpeedTests
{
    private readonly ITestOutputHelper _out;
    public ArmThrowSpeedTests(ITestOutputHelper output) => _out = output;

    private static readonly AbilityEffectCoefficients Effect = new();

    private static FielderAttributes Fielder(int arm, AbilityEffectCoefficients? effect = null)
        => new Player { Name = "x", Position = FieldPosition.Shortstop, ArmStrength = arm }.ToFielder(effect ?? Effect);

    private static double Kmh(int arm) => Fielder(arm).ThrowSpeedMps * 3.6;

    [Theory]
    [InlineData(20, 100.0)]
    [InlineData(50, 115.0)]
    [InlineData(80, 130.0)]
    [InlineData(100, 140.0)]
    public void ThrowSpeed_FollowsDisplayedArm(int arm, double expectedKmh)
        => Assert.Equal(expectedKmh, Kmh(arm), 9);

    [Fact]
    public void ArmIsNotFlattenedByFieldingEffect()
    {
        // 守備・捕球・送球精度は従来どおり寝る（肩だけ外す）。
        var f = new Player { Name = "x", Position = FieldPosition.Shortstop, ArmStrength = 90, Fielding = 90, ThrowAccuracy = 90, Catching = 90 }
            .ToFielder(Effect);
        Assert.Equal(90, f.ArmStrength);
        Assert.Equal(Effect.Fielding(90), f.Fielding);
        Assert.Equal(Effect.Fielding(90), f.ThrowAccuracy);
        Assert.Equal(Effect.Fielding(90), f.Catching);
        Assert.True(Effect.Fielding(90) < 90);
        // 肩50（リーグ平均）は不動点＝統計帯の母集団は変わらない。
        Assert.Equal(50, Fielder(50).ArmStrength);
    }

    [Fact]
    public void StrongArm_ThrowsOutMoreDeepGrounders()
    {
        // 三遊間寄りの深い遊撃ゴロ。チーム全員の肩だけを変え、送球が間に合わなかった率（内野安打＋お手玉失策）を比べる。
        var field = new FieldGeometry();
        var aero = new Aerodynamics();
        var coeff = new FieldingCoefficients { ErrorBaseProb = 0, ThrowErrorBaseProb = 0 };
        var batter = BatterAttributes.LeagueAverage;

        double SafeRate(int arm, double ev, double bearing, AbilityEffectCoefficients? effect = null)
        {
            var safe = 0;
            const int seeds = 300;
            for (ulong s = 1; s <= seeds; s++)
            {
                var p = FieldingResolver.ResolveDetailed(
                    new BattedBall { ExitVelocityMps = ev, LaunchAngleDeg = 4.0, BearingDeg = bearing },
                    field, aero, batter, field.StandardAlignment(Fielder(arm, effect)), coeff, new Xoshiro256Random(s * 7919));
                if (p.Result == BattedBallResult.Single || p.ErrorKind == FieldingErrorKind.Bobble) safe++;
            }
            return (double)safe / seeds;
        }

        // 旧方式（肩も守備と同じ 0.45 で寝かせる）。
        var flattened = Effect with { ArmScale = Effect.FieldingScale };
        var best = 0.0;
        var bestOld = 0.0;
        foreach (var ev in new[] { 28.0, 32.0, 36.0 })
        foreach (var bearing in new[] { -20.0, -16.0, -12.0 })
        {
            var weak = SafeRate(20, ev, bearing);
            var strong = SafeRate(90, ev, bearing);
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "初速{0}m/s 方位{1}°: 肩20 セーフ{2:P1} / 肩90 セーフ{3:P1}", ev, bearing, weak, strong));
            Assert.True(strong <= weak, $"肩が強いほうがセーフが多い（初速{ev} 方位{bearing}）");
            if (weak - strong > best) best = weak - strong;
            var gapOld = SafeRate(20, ev, bearing, flattened) - SafeRate(90, ev, bearing, flattened);
            if (gapOld > bestOld) bestOld = gapOld;
        }
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture, "最大差: 今 {0:P1} / 旧(肩0.45) {1:P1}", best, bestOld));
        // 肩の強弱で「刺せる／刺せない」が分かれる当たりがあり、旧方式より差がはっきり広い。
        Assert.True(best >= 0.10, $"肩20と肩90でセーフ率の差が最大でも {best:P1}");
        Assert.True(best >= bestOld * 1.5, $"旧方式 {bestOld:P1} に対し今 {best:P1}＝幅が広がっていない");
    }
}

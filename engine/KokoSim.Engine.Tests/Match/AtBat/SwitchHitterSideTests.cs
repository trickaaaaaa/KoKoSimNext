using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// スイッチヒッターは投手の逆の打席に立つ（issue #1019）。右投手→左打席／左投手→右打席。
/// 打席側の判定式は <see cref="HandednessExtensions.EffectiveBats"/> の1か所。
/// </summary>
public sealed class SwitchHitterSideTests
{
    private static readonly FieldGeometry Field = new();

    [Theory]
    [InlineData(Handedness.Switch, Handedness.Right, Handedness.Left)]
    [InlineData(Handedness.Switch, Handedness.Left, Handedness.Right)]
    [InlineData(Handedness.Right, Handedness.Left, Handedness.Right)]
    [InlineData(Handedness.Right, Handedness.Right, Handedness.Right)]
    [InlineData(Handedness.Left, Handedness.Left, Handedness.Left)]
    [InlineData(Handedness.Left, Handedness.Right, Handedness.Left)]
    public void EffectiveBats_SwitchTakesOppositeOfPitcher_OthersUnchanged(Handedness bats, Handedness throws, Handedness expected)
        => Assert.Equal(expected, bats.EffectiveBats(throws));

    [Theory]
    [InlineData(Handedness.Right, 1.0)]    // スイッチ×右投手 → 左打席 → 内角は +x（一塁側）
    [InlineData(Handedness.Left, -1.0)]    // スイッチ×左投手 → 右打席 → 内角は −x（三塁側）
    public void InsideSign_OfSwitch_FollowsEffectiveSide(Handedness pitcherThrows, double expectedSign)
        => Assert.Equal(expectedSign, Handedness.Switch.EffectiveBats(pitcherThrows).InsideSign());

    [Fact]
    public void FirstStepBonus_Switch_AppliesOnlyAgainstRightHander()
    {
        var c = new FieldingCoefficients();
        var batter = BatterAttributes.LeagueAverage;
        double T(Handedness bats, Handedness throws)
            => FieldingResolver.BatterToFirstSeconds(Field, batter with { Bats = bats.EffectiveBats(throws) }, c);

        Assert.Equal(T(Handedness.Left, Handedness.Right), T(Handedness.Switch, Handedness.Right));
        Assert.Equal(T(Handedness.Right, Handedness.Left), T(Handedness.Switch, Handedness.Left));
        Assert.True(T(Handedness.Switch, Handedness.Right) < T(Handedness.Switch, Handedness.Left));
    }

    [Theory]
    [InlineData(Handedness.Left, Handedness.Right)]   // 右投手: スイッチ ≡ 左打者
    [InlineData(Handedness.Right, Handedness.Left)]   // 左投手: スイッチ ≡ 右打者
    public void AtBat_SwitchHitter_IsIdenticalToTheSideItStandsOn(Handedness equivalent, Handedness pitcherThrows)
    {
        // 同じシードで、スイッチの打席は「立っている側の打者」と投球ログ（着弾・打球方向）まで1球残らず一致する
        // ＝引っ張り方向・死球の窓・一塁までの近さが、打席側ひとつで決まっている。
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), PitcherThrows = pitcherThrows };
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 135, Control = 30 };
        var sw = BatterAttributes.LeagueAverage with { Bats = Handedness.Switch };
        var eq = BatterAttributes.LeagueAverage with { Bats = equivalent };
        var balls = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var a = AtBatResolver.ResolveDetailed(sw, pitcher, ctx, new Xoshiro256Random(seed));
            var b = AtBatResolver.ResolveDetailed(eq, pitcher, ctx, new Xoshiro256Random(seed));
            Assert.Equal(b.Result, a.Result);
            Assert.Equal(b.PitchLog!.Count, a.PitchLog!.Count);
            for (var i = 0; i < a.PitchLog.Count; i++)
            {
                Assert.Equal(b.PitchLog[i].Kind, a.PitchLog[i].Kind);
                Assert.Equal(b.PitchLog[i].LocationX, a.PitchLog[i].LocationX);
                Assert.Equal(b.PitchLog[i].BattedBall?.BearingDeg, a.PitchLog[i].BattedBall?.BearingDeg);
                if (a.PitchLog[i].BattedBall is not null) balls++;
            }
        }
        Assert.True(balls > 100, "打球が少なすぎて引っ張り方向を比べられない");
    }

    [Fact]
    public void AtBat_SwitchVsLefty_PullsToThirdBaseSide_LikeRightBatter()
    {
        // 左投手に対するスイッチは右打席＝引っ張りは三塁側（方向の符号が負）。右投手に対しては逆。
        double MeanBearing(Handedness throws)
        {
            var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), PitcherThrows = throws };
            var pitcher = new PitcherAttributes { MaxVelocityKmh = 135, Control = 50 };
            var sw = BatterAttributes.LeagueAverage with { Bats = Handedness.Switch };
            double sum = 0; var n = 0;
            for (ulong seed = 1; seed <= 600; seed++)
            {
                var res = AtBatResolver.ResolveDetailed(sw, pitcher, ctx, new Xoshiro256Random(seed));
                foreach (var p in res.PitchLog!)
                    if (p.Kind == PitchKind.InPlay && p.BattedBall is { } bb) { sum += bb.BearingDeg; n++; }
            }
            Assert.True(n > 100);
            return sum / n;
        }
        Assert.True(MeanBearing(Handedness.Left) < MeanBearing(Handedness.Right),
            "左投手との対戦のスイッチは右打席（引っ張り＝三塁側＝負）になっていない");
    }
}

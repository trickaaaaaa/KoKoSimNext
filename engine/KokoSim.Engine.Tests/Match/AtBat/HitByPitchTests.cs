using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// 死球（HBP, design-14 未決F・2026-07-20）。散布結果が体側ウィンドウ（打者の内角側・高さ帯）へ入り
/// 回避に失敗した球は死球＝打席即終了・打数に数えない・走者はフォース進塁のみ。
/// </summary>
public sealed class HitByPitchTests
{
    private static readonly FieldGeometry Field = new();

    [Fact]
    public void WildPitcher_ProducesHitByPitch_AtPlausibleRate()
    {
        // ノーコン投手（Control 25）×多打席: 死球が発生し、かつ打席の1割を超えるような暴発はしない。
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var batter = BatterAttributes.LeagueAverage;
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 135, Control = 25 };
        var rng = new Xoshiro256Random(21);
        int hbp = 0, total = 4000;
        for (var i = 0; i < total; i++)
        {
            if (AtBatResolver.Resolve(batter, pitcher, ctx, rng) == PlateAppearanceResult.HitByPitch) hbp++;
        }
        Assert.True(hbp > 0, "ノーコン投手で死球が一度も出ない");
        Assert.True(hbp < total / 10, $"死球が多すぎる: {hbp}/{total}");
    }

    [Fact]
    public void PreciseAce_HitsBatters_MuchLessThanWildPitcher()
    {
        // 制球派（Control 85）はノーコン投手より死球が明確に少ない（散布σ駆動＝物理層の帰結）。
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var batter = BatterAttributes.LeagueAverage;
        int Count(int control, ulong seed)
        {
            var pitcher = new PitcherAttributes { MaxVelocityKmh = 140, Control = control };
            var rng = new Xoshiro256Random(seed);
            var n = 0;
            for (var i = 0; i < 4000; i++)
            {
                if (AtBatResolver.Resolve(batter, pitcher, ctx, rng) == PlateAppearanceResult.HitByPitch) n++;
            }
            return n;
        }
        var wild = Count(25, 22);
        var ace = Count(85, 22);
        Assert.True(ace < wild, $"制球派の死球({ace})がノーコン({wild})より少なくない");
    }

    [Fact]
    public void HitByPitch_IsNotAnAtBat_AndAwardsFirstBase()
    {
        Assert.False(PlateAppearanceResult.HitByPitch.IsAtBat());
        Assert.False(PlateAppearanceResult.HitByPitch.IsHit());

        // 走者はフォース進塁のみ（四球と同じ）: 満塁は押し出し、二三塁は不動。
        var loaded = new BaseState { First = new Player(), Second = new Player(), Third = new Player() };
        var (runs, outs) = BaserunningModel.Apply(
            loaded, PlateAppearanceResult.HitByPitch, new Player(), 0, new BaserunningCoefficients(),
            new Xoshiro256Random(1));
        Assert.Equal(1, runs);
        Assert.Equal(0, outs);

        var r2 = new Player();
        var r3 = new Player();
        var second3rd = new BaseState { Second = r2, Third = r3 };
        var batter = new Player();
        var (runs2, _) = BaserunningModel.Apply(
            second3rd, PlateAppearanceResult.HitByPitch, batter, 0, new BaserunningCoefficients(),
            new Xoshiro256Random(1));
        Assert.Equal(0, runs2);
        Assert.Same(r2, second3rd.Second); // 非フォースは不動
        Assert.Same(r3, second3rd.Third);
        Assert.Same(batter, second3rd.First);
    }

    [Fact]
    public void HitByPitch_PitchLog_RecordsHitByPitchKind()
    {
        // 死球で終わる打席の PitchLog 終端は HitByPitch でカウント不変のまま。
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var batter = BatterAttributes.LeagueAverage;
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 135, Control = 25 };
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var res = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, rng);
            if (res.Result != PlateAppearanceResult.HitByPitch) continue;
            Assert.NotNull(res.PitchLog);
            Assert.Equal(PitchKind.HitByPitch, res.PitchLog![^1].Kind);
            return; // 1件確認できれば十分
        }
        Assert.Fail("400シードで死球決着の打席が見つからない");
    }

    [Theory]
    // 捕手から見て +x＝一塁側。右打者は三塁側(−x)・左打者は一塁側(+x)に立つ。スイッチは engine では左打席扱い。
    [InlineData(Handedness.Right, -0.70, true)]   // 右打者・内角の大外れ → 体に当たる
    [InlineData(Handedness.Right, 0.70, false)]   // 右打者・外角の大外れ → ボール
    [InlineData(Handedness.Left, 0.70, true)]     // 左打者・内角の大外れ → 体に当たる
    [InlineData(Handedness.Left, -0.70, false)]   // 左打者・外角の大外れ（issue #987 で死球になっていた）→ ボール
    [InlineData(Handedness.Switch, 0.70, true)]
    [InlineData(Handedness.Switch, -0.70, false)]
    public void HbpWindow_IsOnInsideSide_PerBatterHandedness(Handedness bats, double x, bool expected)
    {
        var c = new PitchingCoefficients();
        Assert.Equal(expected, c.IsInHbpWindow(bats, x, 1.0));
    }

    [Theory]
    [InlineData(Handedness.Right)]
    [InlineData(Handedness.Left)]
    public void HbpWindow_RespectsEdgeAndHeightBand(Handedness bats)
    {
        var c = new PitchingCoefficients();
        var inside = bats.InsideSign();
        Assert.True(c.IsInHbpWindow(bats, inside * c.HbpBodyEdgeM, 1.0));                 // 内縁ちょうどは窓内
        Assert.False(c.IsInHbpWindow(bats, inside * (c.HbpBodyEdgeM - 0.01), 1.0));       // 内縁の手前（ゾーン寄り）は窓外
        Assert.False(c.IsInHbpWindow(bats, inside * 0.70, c.HbpBodyBottomM - 0.01));      // ワンバウンド
        Assert.False(c.IsInHbpWindow(bats, inside * 0.70, c.HbpBodyTopM + 0.01));         // 頭上抜け
    }

    [Theory]
    [InlineData(Handedness.Right, -1.0)]
    [InlineData(Handedness.Left, 1.0)]
    [InlineData(Handedness.Switch, 1.0)]
    public void HitByPitch_Window_IsOnBatterInsideSide(Handedness bats, double sign)
    {
        // 死球の窓は打者の内角側（右打者=−x・左打者/スイッチ=+x）。体と反対側の大外れでは当たらない。
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var batter = BatterAttributes.LeagueAverage with { Bats = bats };
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 135, Control = 25 };
        var edge = ctx.Pitching.HbpBodyEdgeM;
        var found = 0;
        for (ulong seed = 1; seed <= 3000; seed++)
        {
            var res = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, new Xoshiro256Random(seed));
            if (res.Result != PlateAppearanceResult.HitByPitch) continue;
            found++;
            var last = res.PitchLog![^1];
            Assert.True(sign * last.LocationX >= edge, $"{bats}: 死球の着弾X={last.LocationX:F3} が内角側の窓の外");
        }
        Assert.True(found > 0, "死球が1件も出ない");
    }

    [Fact]
    public void InsideSign_MatchesDirectiveAndEdgeAim()
    {
        var c = new KokoSim.Engine.Match.Tactics.TacticsCoefficients();
        foreach (var bats in new[] { Handedness.Right, Handedness.Left, Handedness.Switch })
        {
            var inside = c.DirectiveFor(KokoSim.Engine.Match.Tactics.PitchPolicy.InsideAttack, bats).AimXOffsetM;
            Assert.Equal(bats.InsideSign(), System.Math.Sign(inside));
            Assert.Equal(-bats.InsideSign(), c.EdgeAimFor(bats).OutsideSignX);
        }
    }
}

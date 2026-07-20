using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// 死球（HBP, design-14 未決F・2026-07-20）。散布結果が体側ウィンドウ（X負側・高さ帯）へ入り
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
}

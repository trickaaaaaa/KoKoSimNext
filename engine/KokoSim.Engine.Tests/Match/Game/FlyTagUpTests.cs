using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 犠飛以外のタッチアップ（一塁→二塁 / 二塁→三塁, design-14 P3-11）。
/// 置換前は両方とも <see cref="BaserunningCoefficients.ProductiveOutAdvanceProb"/> の18%固定で、
/// 打球の深さ・外野の肩・走力を一切見ていなかった（浅いライトフライでも一塁走者が18%で二塁へ走る）。
/// 犠飛（三塁→本塁, Issue #90）と同じ物理レースへ置換したので、深さが効くことをここで固定する。
/// </summary>
public sealed class FlyTagUpTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    /// <summary>外野フライ（深さ z[m]・滞空 hang[s]・肩50）。x=0＝中堅方向。</summary>
    private static HomePlayContext Fly(double z, double hang, double x = 0)
        => new(Field,
            new HomePlaySituation(new Vector3D(x, 0, z), hang, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: true);

    /// <summary>N試行して「進塁した回数」を数える（fromBase=1 or 2）。</summary>
    private static int AdvancedCount(int fromBase, HomePlayContext fly, ulong seed, int n = 2000)
    {
        var rng = new Xoshiro256Random(seed);
        var advanced = 0;
        for (var i = 0; i < n; i++)
        {
            var runner = new Player { Speed = 50, Baserunning = 50 };
            var bases = fromBase == 1 ? new BaseState { First = runner } : new BaseState { Second = runner };
            BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, rng, collectMoves: false, fly);
            var reached = fromBase == 1 ? bases.Second : bases.Third;
            if (ReferenceEquals(reached, runner)) advanced++;
        }
        return advanced;
    }

    [Fact]
    public void ShallowFly_FirstRunner_NeverTagsUp()
    {
        // ライトへの浅いフライ（55m）: 二塁への送球が最短＝走者に勝ち目がない。実物でも起きない。
        // 置換前はここで18%（≈360/2000）が二塁へ走っていた。
        Assert.Equal(0, AdvancedCount(1, Fly(55, 2.5, x: 30), seed: 21));
    }

    [Fact]
    public void ShallowFly_SecondRunner_NeverTagsUp()
    {
        // 浅い中堅フライ（55m）では二塁走者も三塁へ行けない（置換前は18%で進んでいた）。
        Assert.Equal(0, AdvancedCount(2, Fly(55, 2.5), seed: 22));
    }

    [Fact]
    public void DeepFly_SecondRunner_UsuallyTagsUp()
    {
        // 深い中堅フライ（105m）: 三塁への送球が長く、二塁走者は十分に間に合う＝実物どおり多数が進む。
        var advanced = AdvancedCount(2, Fly(105, 4.6), seed: 23);
        Assert.True(advanced > 1400, $"深いフライで二塁走者の三進が少なすぎる: {advanced}/2000");
    }

    [Fact]
    public void VeryDeepFly_FirstRunner_CanTagUp()
    {
        // 最深部（115m）のフライなら一塁走者も走れる＝「絶対に走らない」ではない。
        // 105m では 0（DeepFly_SecondRunner_UsuallyTagsUp と同じ深さでも一塁走者は動かない）ことと対にして、
        // 一塁走者のタッチアップが二塁走者より一段深いフライを要求することを固定する。
        Assert.Equal(0, AdvancedCount(1, Fly(105, 4.6), seed: 28));
        var veryDeep = AdvancedCount(1, Fly(115, 5.0), seed: 28);
        Assert.True(veryDeep > 1400, $"最深部のフライで一塁走者の二進が少なすぎる: {veryDeep}/2000");
    }

    [Fact]
    public void TagUpRate_IncreasesWithDepth_ForSecondRunner()
    {
        // 深さの単調性（この置換の主眼）。浅い→中→深で三進率が上がる。
        // 送り判定は閾値の階段なので、条件を固定したこの計測では「行かせない(0)／行かせる(≈86%)」の
        // 二値になる（実試合では深さ・滞空・肩・走力が連続的に散らばるため滑らかな深さ依存になる）。
        var shallow = AdvancedCount(2, Fly(100, 4.4), seed: 24);
        var middle = AdvancedCount(2, Fly(105, 4.6), seed: 24);
        var deep = AdvancedCount(2, Fly(115, 5.0), seed: 24);
        Assert.True(shallow < middle, $"浅い({shallow}) < 中({middle}) になっていない");
        Assert.True(middle < deep, $"中({middle}) < 深い({deep}) になっていない");
    }

    [Fact]
    public void FirstRunnerTagsUp_IsRarerThanSecondRunner_AtSameDepth()
    {
        // 同じ深さなら一塁→二塁の方が送球距離が短い＝二塁→三塁より必ず起こりにくい。
        // 「浅いライトフライで一塁走者がタッチアップする」という報告そのものへの回帰テスト。
        foreach (var (z, hang) in new[] { (105.0, 4.6), (115.0, 5.0) })
        {
            var first = AdvancedCount(1, Fly(z, hang), seed: 25);
            var second = AdvancedCount(2, Fly(z, hang), seed: 25);
            Assert.True(second > 0, $"深さ{z}m で二塁走者すら進まない（比較が無意味）: {second}/2000");
            Assert.True(first < second, $"深さ{z}m で 一塁走者({first}) < 二塁走者({second}) になっていない");
        }
    }

    [Fact]
    public void StrongOutfieldArm_SuppressesTagUp()
    {
        // 肩が強いほど送球が速く到達＝タッチアップは抑えられる（能力が効くことの確認）。
        // 105m は肩で送り判定の可否が分かれる深さ（弱肩＝行かせる／強肩＝自重）。
        var weak = new HomePlayContext(Field,
            new HomePlaySituation(new Vector3D(0, 0, 105), 4.6, new Player { ArmStrength = 20 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: true);
        var strong = new HomePlayContext(Field,
            new HomePlaySituation(new Vector3D(0, 0, 105), 4.6, new Player { ArmStrength = 90 }.ToFielder().ThrowSpeedMps),
            new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: true);
        Assert.True(AdvancedCount(2, strong, seed: 26) < AdvancedCount(2, weak, seed: 26),
            "強肩の方が三進を抑えられていない");
    }

    [Fact]
    public void HoldingRunner_ConsumesNoRandomness()
    {
        // 自重は RNG 非消費（本塁・三塁レースと同じ流儀, 不変条件#2）。
        // 浅いフライ（必ず自重）を挟んでも、挟まない場合と乱数列が一致することで確かめる。
        var withRunner = new Xoshiro256Random(27);
        var bases = new BaseState { First = new Player { Speed = 50, Baserunning = 50 } };
        BaserunningModel.ApplyDetailed(
            bases, PlateAppearanceResult.InPlayOut, new Player(), 0, C, withRunner, collectMoves: false,
            Fly(55, 2.5, x: 30));

        var noRunner = new Xoshiro256Random(27);
        BaserunningModel.ApplyDetailed(
            new BaseState(), PlateAppearanceResult.InPlayOut, new Player(), 0, C, noRunner, collectMoves: false,
            Fly(55, 2.5, x: 30));

        Assert.Equal(noRunner.NextDouble(), withRunner.NextDouble());
    }
}

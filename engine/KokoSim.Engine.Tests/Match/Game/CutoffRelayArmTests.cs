using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 中継（カットマン）の肩連動（issue #526）。中継に入る距離は「肩の送球速度 × CutoffThrowSeconds」で
/// 決まる＝強肩ほど遠くから直接返球できる。旧実装は 60m 固定で肩が判定に一切効かず、
/// しきい値をまたいだ瞬間の損失が**強肩ほど大きい**（実際の野球と逆）という副作用があった。
/// </summary>
public sealed class CutoffRelayArmTests
{
    private static readonly BaserunningCoefficients C = new();

    private static double ThrowSpeed(int arm) => new Player { ArmStrength = arm }.ToFielder().ThrowSpeedMps;

    private static HomePlayResolver.ThrowPath PathFrom(double distanceM, int arm)
        => HomePlayResolver.ResolveThrowPath(
            new HomePlaySituation(new Vector3D(0, 0, distanceM), 2.0, ThrowSpeed(arm)),
            new Vector3D(0, 0, 0), C);

    /// <summary>
    /// 本丸: **同じ距離・同じ打球**で、肩の強い外野手は直接返球・弱い外野手は中継になる。
    /// 65m は Arm50 の到達（60.1m）より遠く、Arm80 の到達（67.9m）より近い＝両者が割れる距離。
    /// </summary>
    [Fact]
    public void SameDistance_StrongArmThrowsDirect_WeakArmUsesRelay()
    {
        Assert.True(PathFrom(65.0, arm: 20).HasRelay, "弱肩(Arm20)は65mで中継に入るはず");
        Assert.True(PathFrom(65.0, arm: 50).HasRelay, "平均(Arm50)は65mで中継に入るはず");
        Assert.False(PathFrom(65.0, arm: 80).HasRelay, "強肩(Arm80)は65mを直接返球できるはず");
        Assert.False(PathFrom(65.0, arm: 99).HasRelay, "強肩(Arm99)は65mを直接返球できるはず");
    }

    /// <summary>
    /// リーグ平均（Arm50）では旧実装の 60m 境界が実質そのまま＝帯の移動を小さく保つための固定。
    /// CutoffThrowSeconds=1.88 × 31.944m/s = 60.06m なので、60m は直接・61m は中継。
    /// </summary>
    [Fact]
    public void AverageArm_KeepsLegacySixtyMeterBoundary()
    {
        Assert.False(PathFrom(60.0, arm: 50).HasRelay, "Arm50・60mは旧実装どおり直接返球");
        Assert.True(PathFrom(61.0, arm: 50).HasRelay, "Arm50・61mは旧実装どおり中継");
    }

    /// <summary>肩が上がるほど中継に入る距離が単調に伸びる（連動の向きを固定する）。</summary>
    [Fact]
    public void RelayDistance_IncreasesMonotonicallyWithArm()
    {
        double Reach(int arm) => ThrowSpeed(arm) * C.CutoffThrowSeconds;

        Assert.True(Reach(20) < Reach(50), "弱肩の到達は平均より短いはず");
        Assert.True(Reach(50) < Reach(80), "平均の到達は強肩より短いはず");
        Assert.True(Reach(80) < Reach(99), "強肩の到達は最強肩より短いはず");
        // 目安（issue #526 の表）: 52m / 60m / 68m / 73m
        Assert.InRange(Reach(20), 51.0, 53.0);
        Assert.InRange(Reach(50), 59.0, 61.0);
        Assert.InRange(Reach(80), 67.0, 69.0);
        Assert.InRange(Reach(99), 72.0, 74.0);
    }

    /// <summary>
    /// 強肩が直接返球へ倒れることで、しきい値をまたいだ損失（旧実装は強肩ほど大きかった）が消える。
    /// 65m で Arm80 は直接返球＝Arm50 の中継より確実に速く着く。
    /// </summary>
    [Fact]
    public void StrongArm_ArrivesEarlierThanAverageArm_AtTheSameDistance()
    {
        var strong = PathFrom(65.0, arm: 80);
        var average = PathFrom(65.0, arm: 50);

        Assert.False(strong.HasRelay);
        Assert.True(average.HasRelay);
        Assert.True(strong.ArriveAtSeconds < average.ArriveAtSeconds,
            $"強肩の直接返球が平均の中継より遅い: strong={strong.ArriveAtSeconds:F3} average={average.ArriveAtSeconds:F3}");
    }

    /// <summary>
    /// 中継が消滅していないこと（案Bを採らなかった理由の担保）。十分遠ければ最強肩でも中継に入る。
    /// 「直接と中継の速い方を選ぶ」に置き換えると Arm51 以上で中継が絶対に選ばれなくなる。
    /// </summary>
    [Fact]
    public void EvenStrongestArm_StillUsesRelay_WhenFarEnough()
        => Assert.True(PathFrom(80.0, arm: 99).HasRelay, "80mは最強肩でも中継に入るはず");

    /// <summary>判定式は1箇所（<see cref="HomePlayResolver.NeedsRelay"/>）＝純関数として直接も固定する。</summary>
    [Theory]
    [InlineData(59.0, 50, false)]
    [InlineData(61.0, 50, true)]
    [InlineData(65.0, 80, false)]
    [InlineData(70.0, 80, true)]
    public void NeedsRelay_IsThePureFunctionBehindBothJudgementAndReplay(
        double distanceM, int arm, bool expected)
        => Assert.Equal(expected, HomePlayResolver.NeedsRelay(distanceM, ThrowSpeed(arm), C));
}

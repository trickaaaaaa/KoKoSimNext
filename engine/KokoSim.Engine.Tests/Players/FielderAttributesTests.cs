using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// トランスファー(捕球→送球の握り替え)秒数の能力紐づけ（設計書02 §1.2, Issue #36）。
/// 守備(Fielding)を選ぶオーナー決定（issueコメント「A」）に基づき、Fielding=50でbaseSecondsと恒等
/// （帯不変, 不変条件#5）、守備が高いほど短縮、床(floor)未満へは落ちないことを検証する。
/// </summary>
public sealed class FielderAttributesTests
{
    [Fact]
    public void TransferSeconds_AtFieldingFifty_EqualsBaseSeconds()
    {
        var a = new FielderAttributes { Fielding = 50 };
        Assert.Equal(0.70, a.TransferSeconds(0.70, 0.003, 0.15), 9);
    }

    [Fact]
    public void TransferSeconds_HigherFielding_IsShorter()
    {
        var low = new FielderAttributes { Fielding = 20 };
        var high = new FielderAttributes { Fielding = 90 };
        Assert.True(
            high.TransferSeconds(0.70, 0.003, 0.15) < low.TransferSeconds(0.70, 0.003, 0.15),
            "守備が高いほどトランスファーが短くなっていない");
    }

    [Fact]
    public void TransferSeconds_ScalesLinearlyWithFieldingOffset()
    {
        var a = new FielderAttributes { Fielding = 80 };
        Assert.Equal(0.70 - (80 - 50) * 0.003, a.TransferSeconds(0.70, 0.003, 0.15), 9);
    }

    [Fact]
    public void TransferSeconds_ClampsAtFloor()
    {
        // 0.20 − (100−50)×0.01 = −0.30 → floor 0.15 でクランプされる。
        var a = new FielderAttributes { Fielding = 100 };
        Assert.Equal(0.15, a.TransferSeconds(0.20, 0.01, 0.15), 9);
    }
}

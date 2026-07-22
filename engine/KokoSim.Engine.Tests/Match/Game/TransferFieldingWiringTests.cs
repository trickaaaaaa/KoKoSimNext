using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// トランスファー(捕球→送球の握り替え)秒数の守備(Fielding)紐づけ（Issue #36, design-02 §1.2）。
/// オーナー決定（issueコメント「A」）で Fielding へ紐づけた。守備50でbaseSecondsと恒等（帯不変, 不変条件#5）、
/// 守備が高いほど短縮することを、捕手ポップ（<see cref="StealResolver"/>）と外野返球
/// （<see cref="HomePlayResolver"/>）の双方で検証する。中継(Relay)は具体的な選手識別を持たないため対象外。
/// </summary>
public sealed class TransferFieldingWiringTests
{
    private static Player Catcher(int fielding) => new() { Fielding = fielding, ArmStrength = 50 };

    // --- 捕手ポップ（StealResolver.PopTransferSeconds） ---

    [Fact]
    public void StealDefenseTime_AtFieldingFifty_TransferSlopeHasNoEffect()
    {
        var catcher = Catcher(50);
        var c = new BaserunningCoefficients();
        var withSlope = StealResolver.DefenseTimeSeconds(catcher, c);
        var withoutSlope = StealResolver.DefenseTimeSeconds(catcher, c with { TransferFieldingSlope = 0 });
        Assert.Equal(withSlope, withoutSlope, 9);
    }

    [Fact]
    public void StealDefenseTime_HigherCatcherFielding_IsFaster()
    {
        var c = new BaserunningCoefficients();
        var slow = StealResolver.DefenseTimeSeconds(Catcher(20), c);
        var fast = StealResolver.DefenseTimeSeconds(Catcher(90), c);
        Assert.True(fast < slow, $"守備が高い捕手のポップが速くなっていない: slow={slow} fast={fast}");
    }

    // --- 外野の捕球→送球（HomePlayResolver.OutfieldTransferSeconds） ---

    private static HomePlaySituation Situation(int outfielderFielding)
        => new(new Vector3D(0, 0, 50), 2.0, 32.0, outfielderFielding);

    [Fact]
    public void HomePlayDefenseTime_AtFieldingFifty_TransferSlopeHasNoEffect()
    {
        var s = Situation(50);
        var c = new BaserunningCoefficients();
        var withSlope = HomePlayResolver.DefenseTimeSeconds(s, c);
        var withoutSlope = HomePlayResolver.DefenseTimeSeconds(s, c with { TransferFieldingSlope = 0 });
        Assert.Equal(withSlope, withoutSlope, 9);
    }

    [Fact]
    public void HomePlayDefenseTime_HigherOutfielderFielding_IsFaster()
    {
        var c = new BaserunningCoefficients();
        var slow = HomePlayResolver.DefenseTimeSeconds(Situation(20), c);
        var fast = HomePlayResolver.DefenseTimeSeconds(Situation(90), c);
        Assert.True(fast < slow, $"守備が高い外野手の返球が速くなっていない: slow={slow} fast={fast}");
    }

    [Fact]
    public void HomePlaySituation_DefaultOutfielderFieldingAbility_IsFifty()
    {
        // 3引数コンストラクタ（既存呼び出し・大量のテストが使用）は既定50＝恒等を保つ。
        var withoutAbility = new HomePlaySituation(new Vector3D(0, 0, 50), 2.0, 32.0);
        var c = new BaserunningCoefficients();
        Assert.Equal(
            HomePlayResolver.DefenseTimeSeconds(Situation(50), c),
            HomePlayResolver.DefenseTimeSeconds(withoutAbility, c), 9);
    }
}

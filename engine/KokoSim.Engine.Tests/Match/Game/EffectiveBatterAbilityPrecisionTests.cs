using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 試合中の実効打者能力（<see cref="BatterAttributes"/>）は丸めない（issue #1631）。
/// プレッシャー等の倍率chain・打撃姿勢・速球打ち/変化球打ちの補正は、物理層へ渡る直前まで
/// double のまま積み重なり、修正前のように段ごとに int へ丸めて小さな効きが消えたり、
/// 掛ける順で結果が変わったりしないことを固定する。
/// </summary>
public sealed class EffectiveBatterAbilityPrecisionTests
{
    /// <summary>
    /// 小さな倍率（制球/ミート50×1.008）が、プレッシャー→打撃姿勢→速球打ちの3段を経ても
    /// 丸めで消えずに物理層の入力（最終 Contact）へ届くこと。修正前（各段で int に丸める実装）だと
    /// 1段目の 50.4 が即 50 に丸まり、以降の補正もすべて 50 基準になってズレる。
    /// </summary>
    [Fact]
    public void SmallMultiplier_SurvivesThroughFullCorrectionChain()
    {
        var b0 = BatterAttributes.LeagueAverage; // Contact = 50

        // 1段目: プレッシャー→主将→動揺→伝令→相性→勢いの倍率chainの代表として、小さな倍率を直接掛ける
        // （GameEngine.EffectiveMatchup の最終 PressureModel.ApplyBatter と同じ入口）。
        var b1 = PressureModel.ApplyBatter(b0, 1.008);
        Assert.Equal(50.4, b1.Contact, precision: 9);

        // 2段目: 打撃姿勢（つなぎの打撃 PutInPlay）。Power側の補正は0にして Contact だけを見る。
        var tactics = new TacticsCoefficients { PutInPlayContactBoost = 0.003, PutInPlayPowerPenalty = 0.0 };
        var b2 = BattingApproachModel.Apply(b1, BattingApproach.PutInPlay, tactics);
        var expectedAfterApproach = 50.4 * 1.003;
        Assert.Equal(expectedAfterApproach, b2.Contact, precision: 9);

        // 3段目: 速球打ち（issue #907）の加算補正。
        var mods = SkillPlayMods.None with { FastballContactBonus = 0.05 };
        var b3 = mods.ForPitch(b2, PitchType.Fastball);
        var expectedFinal = expectedAfterApproach + 0.05;
        Assert.Equal(expectedFinal, b3.Contact, precision: 9);

        // 丸めていれば 50 or 51 のどちらかに落ちてしまう小さな効き（0.5点未満）が、分数のまま残っている。
        Assert.NotEqual(50.0, b3.Contact);
        Assert.NotEqual(51.0, b3.Contact);
    }

    /// <summary>
    /// 2つの倍率補正（プレッシャー相当・打撃姿勢）を適用する順序を入れ替えても、最終 Contact は一致する
    /// （丸めない double の掛け合わせは可換）。修正前は段ごとに int へ丸めていたため、
    /// 50×1.02→52 周り→さらに×1.01 と 50×1.01→50 周り→さらに×1.02 で結果がズレ得た。
    /// </summary>
    [Fact]
    public void ApplyingOrder_OfTwoSmallMultipliers_DoesNotChangeFinalContact()
    {
        var b0 = BatterAttributes.LeagueAverage;
        var tactics = new TacticsCoefficients { PutInPlayContactBoost = 0.01, PutInPlayPowerPenalty = 0.0 };

        // 順序A: プレッシャー相当 → 打撃姿勢
        var a1 = PressureModel.ApplyBatter(b0, 1.02);
        var a2 = BattingApproachModel.Apply(a1, BattingApproach.PutInPlay, tactics);

        // 順序B: 打撃姿勢 → プレッシャー相当
        var b1 = BattingApproachModel.Apply(b0, BattingApproach.PutInPlay, tactics);
        var b2 = PressureModel.ApplyBatter(b1, 1.02);

        Assert.Equal(a2.Contact, b2.Contact, precision: 9);
    }
}

using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 名声の時間応答（設計書04 §1.2）。
///
/// <para>2026-08-15 案2 で「速さ」（減衰0.80/勝4.0/甲30・時定数5年）を入れたが、実ゲームが数える
/// 公式戦勝数は sim（夏の県大会＋甲子園だけ）の約2倍（秋の県・地区も数える）で、
/// 定常＝勝数×20 の線形和では強さ60台の学校が名声Sに張り付き、県制覇の年は1年で3段飛んでいた。
/// 2026-08-26 案C+D で上昇側にだけ①逓減 ②年間上限 を入れて寝かせた。ここではその形を固定する。</para>
/// </summary>
public sealed class FameDynamicsTests
{
    private static readonly CareerCoefficients C = new();

    private static double RunYears(int years, int winsPerYear, bool koshien, double startFame = 0)
    {
        var m = new Manager { Fame = startFame };
        for (var y = 0; y < years; y++)
        {
            ManagerSeasonUpdate.Apply(m, reachedKoshien: koshien, nationalChampion: false, wins: winsPerYear,
                milestoneRng: new Xoshiro256Random(0xFA3E_0000UL + (ulong)y), careerCoeff: C);
        }
        return m.Fame;
    }

    /// <summary>逓減つきの定常値 = 100g/(20+g)（g＝年間の上昇量）＝100へ張り付かない。</summary>
    private static double SteadyState(double gainPerYear)
        => C.FameGainCeiling * gainPerYear / (C.FameGainCeiling * (1 - C.FameDecay) + gainPerYear);

    [Fact]
    public void SteadyState_SaturatesBelowTheCeiling()
    {
        // 年5勝（実ゲームの強さ65≒C相当の実測値）で定常 50 前後＝D帯の手前で落ち着く。
        // 旧（線形）では 5×20＝100 に張り付いてS帯を占有していた。
        var expected5 = SteadyState(C.FamePerWin * 5);
        Assert.InRange(expected5, 45.0, 55.0);
        Assert.InRange(RunYears(40, winsPerYear: 5, koshien: false), expected5 - 1.0, expected5 + 0.01);

        // 年10勝（強さ75≒B相当）でも定常は 65〜70＝A・S は甲子園を積んだ監督のために空いたまま。
        var expected10 = SteadyState(C.FamePerWin * 10);
        Assert.InRange(expected10, 60.0, 72.0);
        Assert.InRange(RunYears(40, winsPerYear: 10, koshien: false), expected10 - 1.0, expected10 + 0.01);

        // 勝ち続けるだけでは100に届かない（甲子園の実績が要る）。
        Assert.True(RunYears(40, winsPerYear: 15, koshien: false) < 90.0);
    }

    [Fact]
    public void NeverJumpsMoreThanOneGradePerYear()
    {
        // 案D: 正味の上昇は年 FameSeasonRiseCap まで。等級の帯幅は10なので1年で2段は飛ばない。
        // 県大会を制した年の最大級の入力（13勝＋甲子園＋全国優勝＋金星上限）でも守られること。
        foreach (var start in new[] { 0.0, 20.0, 35.0, 50.0, 70.0, 90.0 })
        {
            var next = CareerEngine.NextFame(
                start, reachedKoshien: true, nationalChampion: true, wins: 13,
                upsetFameDelta: C.FameUpsetSeasonCap, C);
            Assert.True(next - start <= C.FameSeasonRiseCap + 1e-9,
                $"名声 {start:F0} からの1年の上昇が上限を超えた: +{next - start:F1}");
        }
    }

    [Fact]
    public void KoshienStillFeelsLikeAnEvent()
    {
        // 寝かせても「甲子園に出た年は跳ねる」は残す＝上限いっぱいまで上がる（勝ちだけの年より大きい）。
        var koshienYear = CareerEngine.NextFame(35, true, false, wins: 13, upsetFameDelta: 0, C);
        var winsOnlyYear = CareerEngine.NextFame(35, false, false, wins: 5, upsetFameDelta: 0, C);
        Assert.True(koshienYear - 35 >= C.FameSeasonRiseCap - 1e-9);
        Assert.True(koshienYear > winsOnlyYear);
    }

    [Fact]
    public void RisesWithinAGeneration_ForAGrowingProgram()
    {
        // 成長型（年2勝を5年）で 5年目に 20〜28＝旧値（0.92/1.5）では届かなかった帯（E帯の新入生が
        // 入り始める水準）に一世代で入る。定常 100×8/(20+8)=28.6 の8割まで来ている。
        var fame5 = RunYears(5, winsPerYear: 2, koshien: false);
        Assert.InRange(fame5, 20.0, 28.6);
    }

    [Fact]
    public void DecaysWithinAGeneration_WhenTheWinningStops()
    {
        // 王朝も勝てなくなれば一世代で崩れる: 100 → 年1勝を6年で 40 前後。
        // 逓減・年間上限はどちらも上昇側だけなので、下降側の速さは案2から変わらない。
        var fame = RunYears(6, winsPerYear: 1, koshien: false, startFame: 100);
        Assert.InRange(fame, 30.0, 48.0);
    }

    [Fact]
    public void UpsetLoss_IsNotSoftenedByHighFame()
    {
        // 逓減は上昇側だけに掛ける（名声が高いほど取りこぼしの罰が軽くなるのは逆）。
        // 勝ちも甲子園も無く負のデルタだけの年は、減衰＋デルタがそのまま出る。
        var next = CareerEngine.NextFame(80, false, false, wins: 0, upsetFameDelta: -7.0, C);
        Assert.Equal(80 * C.FameDecay - 7.0, next, 6);
    }
}

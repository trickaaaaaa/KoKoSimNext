using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 名声の時間応答（2026-08-15 案2・設計書04 §1.2）。旧値（減衰0.92/勝1.5/甲22）は時定数12.5年で
/// 「勝っても名声が動かない」体感だった。新値（減衰0.80/勝4.0/甲30）は時定数5年・定常＝勝数×20 で、
/// 上がりも下がりも一世代（3〜6年）で見える。ここではその形（速さと定常）を係数のまま固定する。
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

    [Fact]
    public void SteadyState_IsWinsTimesTwenty()
    {
        // 定常値 = FamePerWin×勝数/(1−FameDecay) = 勝数×20（旧 18.75 と同等＝長期の水準は変えていない）。
        Assert.Equal(20.0, C.FamePerWin / (1.0 - C.FameDecay), 6);
        // 年3勝を続けると 60（C帯の新入生が入る名声）に収束する。10年で定常の 89%（1−0.8^10）。
        var fame = RunYears(10, winsPerYear: 3, koshien: false);
        Assert.InRange(fame, 60.0 * (1 - System.Math.Pow(C.FameDecay, 10)) - 0.5, 60.0);
    }

    [Fact]
    public void RisesWithinAGeneration_ForAGrowingProgram()
    {
        // 成長型（年2勝を5年）で 5年目に 20〜30＝旧値（12）では届かなかった E 帯の新入生が入り始める。
        var fame5 = RunYears(5, winsPerYear: 2, koshien: false);
        Assert.InRange(fame5, 20.0, 30.0);
        // 甲子園に出れば1年で +30 以上（勝数ぶんも乗る）＝出場が「事件」として名声に刻まれる。
        var m = new Manager { Fame = 40 };
        ManagerSeasonUpdate.Apply(m, reachedKoshien: true, nationalChampion: false, wins: 5,
            milestoneRng: new Xoshiro256Random(1), careerCoeff: C);
        Assert.True(m.Fame >= 40 * C.FameDecay + C.FameKoshienAppearance + 5 * C.FamePerWin - 1e-9);
        Assert.True(m.Fame > 70.0, $"甲子園出場の年に名声が跳ねていない: {m.Fame:F1}");
    }

    [Fact]
    public void DecaysWithinAGeneration_WhenTheWinningStops()
    {
        // 王朝も勝てなくなれば一世代で崩れる: 100 → 年1勝を6年で 40 前後（旧値なら 68 で残っていた）。
        var fame = RunYears(6, winsPerYear: 1, koshien: false, startFame: 100);
        Assert.InRange(fame, 35.0, 48.0);
    }
}

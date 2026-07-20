using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>
/// 打者判断（設計書15 Phase E-3）の物理妥当性テスト。誘発変化合成量がゾーン内外で対称（符号反転のみ）に
/// 効くこと、ゾーン外の釣られ確率がゾーンからの距離に対して単調減少のままであることを固定する。
/// </summary>
public sealed class BatterDecisionTests
{
    private static readonly BattingCoefficients Coeff = new();
    private static readonly BatterAttributes Batter = BatterAttributes.LeagueAverage;

    [Fact]
    public void SwingProbability_BreakMagnitude_IsSymmetricAcrossZoneBoundary()
    {
        // ゾーン内−／ゾーン外+が同一係数で効くことを固定（ユーザー承認: 対称性を崩す非対称化はしない）。
        // ゾーン外側は distanceOutsideM=0（境界上）に固定し、距離減衰項を切り離して係数の対称性だけを見る。
        const double breakMagnitude = 0.4;

        var baseInZone = BatterDecision.SwingProbability(true, 0.0, 0.0, Batter, Coeff);
        var withBreakInZone = BatterDecision.SwingProbability(true, 0.0, breakMagnitude, Batter, Coeff);

        var baseOutOfZone = BatterDecision.SwingProbability(false, 0.0, 0.0, Batter, Coeff);
        var withBreakOutOfZone = BatterDecision.SwingProbability(false, 0.0, breakMagnitude, Batter, Coeff);

        var deltaInZone = withBreakInZone - baseInZone;
        var deltaOutOfZone = withBreakOutOfZone - baseOutOfZone;

        Assert.True(deltaInZone < 0, "ゾーン内は変化量が大きいほど見送り方向（スイング率低下）のはず");
        Assert.True(deltaOutOfZone > 0, "ゾーン外は変化量が大きいほど釣られる方向（スイング率上昇）のはず");
        Assert.Equal(-deltaInZone, deltaOutOfZone, 9); // 同一係数・符号反転のみ
    }

    [Fact]
    public void SwingProbability_ChaseAtMaxBreak_StillDecaysMonotonically_WithDistanceFromZone()
    {
        // 大外れの球が変化量だけで釣れる不自然を防ぐガード（ユーザー指定の物理妥当性テスト）。
        const double maxBreakMagnitude = 1.0; // 現実的なレンジの上限相当

        // 下限クランプ(0.02)へ張り付く領域では等しくなり得るため非増加（単調非増加）で判定する。
        // クランプ前の領域（0.0〜0.3）では厳密な減少も別途確認する。
        var distances = new[] { 0.0, 0.1, 0.2, 0.3, 0.5, 0.8, 1.2 };
        double? previous = null;
        foreach (var d in distances)
        {
            var p = BatterDecision.SwingProbability(false, d, maxBreakMagnitude, Batter, Coeff);
            if (previous is not null)
            {
                Assert.True(p <= previous, $"distance={d}: チェイス確率 {p} は直前の距離({previous})以下でなければならない");
            }
            previous = p;
        }

        var pNear = BatterDecision.SwingProbability(false, 0.0, maxBreakMagnitude, Batter, Coeff);
        var pFar = BatterDecision.SwingProbability(false, 0.3, maxBreakMagnitude, Batter, Coeff);
        Assert.True(pFar < pNear, "クランプ前の領域では距離が離れるほど厳密に低くなるはず");
    }

    [Fact]
    public void SwingProbability_ChaseIncreasesWithBreakMagnitude_AtFixedDistance()
    {
        var magnitudes = new[] { 0.0, 0.1, 0.2, 0.3, 0.5 };
        double? previous = null;
        foreach (var m in magnitudes)
        {
            var p = BatterDecision.SwingProbability(false, 0.1, m, Batter, Coeff);
            if (previous is not null)
            {
                Assert.True(p > previous, $"magnitude={m}: チェイス確率 {p} は直前の変化量({previous})より高くなければならない");
            }
            previous = p;
        }
    }

    [Fact]
    public void SwingProbability_InZone_DistanceHasNoEffect()
    {
        // ゾーン内は常に distanceOutsideM=0 が渡される想定だが、仮に非0が渡っても効かないことを固定
        // （距離減衰はゾーン外専用のガードであり、ゾーン内の見誤り確率には無関係）。
        var atZero = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, Coeff);
        var atNonZero = BatterDecision.SwingProbability(true, 0.5, 0.3, Batter, Coeff);

        Assert.Equal(atZero, atNonZero, 9);
    }
}

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

    [Fact]
    public void SwingProbability_InZone_DecreasesLinearlyWithEdgeProximity()
    {
        // 「隅のいい球は見逃されやすい」（issue #403）: 縁への近さに対して線形に p_swing が下がる。
        var center = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, Coeff, zoneEdgeProximity: 0.0);
        var half = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, Coeff, zoneEdgeProximity: 0.5);
        var edge = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, Coeff, zoneEdgeProximity: 1.0);

        Assert.True(edge < half && half < center, "縁に近いほどスイング率が下がるはず");
        Assert.Equal(Coeff.ZoneEdgeSwingPenalty, center - edge, 9);          // 縁上で係数ぶんちょうど見送る
        Assert.Equal(Coeff.ZoneEdgeSwingPenalty * 0.5, center - half, 9);    // 線形
    }

    [Fact]
    public void SwingProbability_EdgeProximityDefault_IsIdentity()
    {
        // 既定 0.0（＝中心）は従来と1ビットも変わらない（引数を渡さない既存テスト・呼び出しの互換保証）。
        var withoutArg = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, Coeff);
        var explicitZero = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, Coeff, zoneEdgeProximity: 0.0);

        Assert.Equal(withoutArg, explicitZero, 9);
    }

    [Fact]
    public void SwingProbability_OutOfZone_EdgeProximityHasNoEffect()
    {
        // ゾーン外では縁減衰は掛からない（チェイス側は ChaseDistanceSlope の距離減衰が同役を担う）。
        var atZero = BatterDecision.SwingProbability(false, 0.1, 0.3, Batter, Coeff, zoneEdgeProximity: 0.0);
        var atOne = BatterDecision.SwingProbability(false, 0.1, 0.3, Batter, Coeff, zoneEdgeProximity: 1.0);

        Assert.Equal(atZero, atOne, 9);
    }

    // --- カウント依存のスイング判断（設計書01 §2③「カウントで判定」、見逃し三振率の調査） ---

    [Fact]
    public void SwingProbability_CountCoefficientsAtIdentity_StrikesAndBallsHaveNoEffect()
    {
        // 2ストライク/3-0系の係数を明示的に恒等（boost=0, factor=1）にすれば、strikes/balls を
        // 渡しても渡さなくても結果は同一（既存呼び出しへの後方互換の担保。実際の既定値は校正済みで
        // 恒等ではない＝Coeff（既定 new()）ではなく明示した係数束で検証する）。
        var identityCoeff = Coeff with { TwoStrikeSwingBoost = 0.0, ThreeZeroSwingFactor = 1.0 };

        var inZoneBase = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, identityCoeff, strikes: 0, balls: 0);
        var inZoneTwoStrikes = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, identityCoeff, strikes: 2, balls: 1);
        Assert.Equal(inZoneBase, inZoneTwoStrikes, 9);

        var outOfZoneBase = BatterDecision.SwingProbability(false, 0.1, 0.3, Batter, identityCoeff, strikes: 0, balls: 0);
        var outOfZoneThreeZero = BatterDecision.SwingProbability(false, 0.1, 0.3, Batter, identityCoeff, strikes: 0, balls: 3);
        Assert.Equal(outOfZoneBase, outOfZoneThreeZero, 9);
    }

    [Fact]
    public void SwingProbability_TwoStrikes_BoostsBothZones_OnlyAtTwoOrMoreStrikes()
    {
        // 1変数原則（ChaseBreakSlope と同じ）: ゾーン内外どちらにも同一値を加点する。
        var coeff = Coeff with { TwoStrikeSwingBoost = 0.15 };

        var inZoneZero = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, coeff, strikes: 0);
        var inZoneOne = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, coeff, strikes: 1);
        var inZoneTwo = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, coeff, strikes: 2);
        Assert.Equal(inZoneZero, inZoneOne, 9);
        Assert.Equal(coeff.TwoStrikeSwingBoost, inZoneTwo - inZoneZero, 9);

        var outZoneZero = BatterDecision.SwingProbability(false, 0.1, 0.3, Batter, coeff, strikes: 0);
        var outZoneTwo = BatterDecision.SwingProbability(false, 0.1, 0.3, Batter, coeff, strikes: 2);
        Assert.Equal(coeff.TwoStrikeSwingBoost, outZoneTwo - outZoneZero, 9);
    }

    [Fact]
    public void SwingProbability_ThreeZero_ScalesDownSwingRate_OnlyAtThreeBallsZeroStrikes()
    {
        var coeff = Coeff with { ThreeZeroSwingFactor = 0.15 };

        var inZoneNormal = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, coeff, strikes: 0, balls: 2);
        var inZoneThreeZero = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, coeff, strikes: 0, balls: 3);
        Assert.Equal(inZoneNormal * coeff.ThreeZeroSwingFactor, inZoneThreeZero, 9);
        Assert.True(inZoneThreeZero < inZoneNormal, "3-0はスイング率が下がるはず");

        var outZoneNormal = BatterDecision.SwingProbability(false, 0.1, 0.3, Batter, coeff, strikes: 0, balls: 2);
        var outZoneThreeZero = BatterDecision.SwingProbability(false, 0.1, 0.3, Batter, coeff, strikes: 0, balls: 3);
        Assert.Equal(outZoneNormal * coeff.ThreeZeroSwingFactor, outZoneThreeZero, 9);
    }

    [Fact]
    public void SwingProbability_ThreeZero_DoesNotApply_WhenStrikesIsNonZero()
    {
        // 3ボール2ストライク（フルカウント）は「3ボール」だが「0ストライク」ではないので対象外
        // （フルカウントで待てにする必要はない＝別のロジック）。
        var coeff = Coeff with { ThreeZeroSwingFactor = 0.15 };

        var fullCount = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, coeff, strikes: 2, balls: 3);
        var twoStrikesOnly = BatterDecision.SwingProbability(true, 0.0, 0.3, Batter, coeff, strikes: 2, balls: 1);

        Assert.Equal(twoStrikesOnly, fullCount, 9);
    }
}

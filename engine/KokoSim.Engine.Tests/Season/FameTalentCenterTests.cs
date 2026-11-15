using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 監督の名声→新入生の質(talentCenter)フィードバック（issue #127・設計書04 §2.1、issue #1031で写像を再校正）。
/// 「勝つ→名声→良い新入生」の正フィードバックに、暴走防止の頭打ち（cap）が効いていることを検証する。
/// </summary>
public sealed class FameTalentCenterTests
{
    private static readonly RosterCoefficients C = new();

    [Fact]
    public void TalentCenter_IsIdentity_BelowKnee()
    {
        // issue #1031: knee以下は恒等（center=fame）＝名声のランクと中心値のランクが厳密に一致する。
        // Fame=0（または未注入）が TalentCenterDefault と一致するという旧要件は、実際には名声未注入(null)の
        // 別経路でしか使われない一致だったので撤回した（SeasonEngine は Fame=null のときこの関数を呼ばない）。
        foreach (var fame in new double[] { C.TalentCenterDefault, 40, 50, 55, 60, C.FameTalentCenterKnee })
            Assert.Equal(fame, C.TalentCenterFromFame(fame), 9);
    }

    [Fact]
    public void TalentCenter_HasFloorAtDefault_ForLowFame()
    {
        // issue #1043: 名声0〜Default は中心 Default(32)＝F相当。新規ゲームの初期名声10でも中心10にならない。
        foreach (var fame in new double[] { 0, 10, 20, 31.9, -5 })
            Assert.Equal(C.TalentCenterDefault, C.TalentCenterFromFame(fame), 9);
        Assert.Equal(Tier.F, Tiers.FromStrength(C.TalentCenterFromFame(10)));
        Assert.InRange(C.TalentCenterFromFame(55), 50.0, 59.999);
    }

    [Fact]
    public void TalentCenter_IncreasesMonotonically_WithFame()
    {
        var prev = C.TalentCenterFromFame(0);
        foreach (var fame in new double[] { 10, 25, 50, 75, 100 })
        {
            var cur = C.TalentCenterFromFame(fame);
            // 下限(Default)の区間は平らなので、下限を超えた名声から狭義に増えることを見る
            if (fame <= C.TalentCenterDefault) { Assert.True(cur >= prev); prev = cur; continue; }
            Assert.True(cur > prev, $"Fame={fame} でtalentCenterが増えていない（{prev}→{cur}）");
            prev = cur;
        }
    }

    [Fact]
    public void TalentCenter_CapsNearSpan_AtMaxFame()
    {
        // 頭打ち（cap）は実装の必須要件（issue #127）。Fame=100でもcap(knee+span)を超えない。
        var atMax = C.TalentCenterFromFame(100);
        Assert.True(atMax <= C.FameTalentCenterCap,
            $"Fame=100のtalentCenter({atMax})が上限({C.FameTalentCenterCap})を超えた");
        // 2026-08-15（案D・オーナー決定、issue #1031でも維持）: 名声を極めた学校には B/A の新入生が入る＝
        // Fame=100 で中心 B 帯（70〜79）。
        Assert.InRange(atMax, 70.0, 79.0);
    }

    [Fact]
    public void TalentCenter_TracksFameTier_ForLowAndMidFame()
    {
        // issue #1031 の完了条件: 名声の各ランクの中央の値の中心値が、同じランクの範囲に入る。
        // knee(65)以下は恒等なので自明に一致するが、ランク境界をまたぐ値でも明示的に固定する。
        Assert.Equal(Tier.F, Tiers.FromStrength(C.TalentCenterFromFame(20)));   // 下限(32)でF（issue #1043。素のままならG）
        Assert.Equal(Tier.F, Tiers.FromStrength(C.TalentCenterFromFame(35)));   // F: 30-39
        Assert.Equal(Tier.E, Tiers.FromStrength(C.TalentCenterFromFame(45)));   // E: 40-49
        Assert.Equal(Tier.D, Tiers.FromStrength(C.TalentCenterFromFame(55)));   // D: 50-59（issue本文の例示）
        Assert.Equal(Tier.C, Tiers.FromStrength(C.TalentCenterFromFame(60)));   // C: 60-69
    }

    [Fact]
    public void TalentCenter_StaysCappedAtB_ForHighFame()
    {
        // 2026-08-15案Dのオーナー決定（暴走防止）は knee を超えた帯でだけ残す＝名声80〜100でも B帯に留まる。
        Assert.Equal(Tier.B, Tiers.FromStrength(C.TalentCenterFromFame(80)));
        Assert.Equal(Tier.B, Tiers.FromStrength(C.TalentCenterFromFame(100)));
    }

    [Fact]
    public void PhysicalCenter_FollowsTalentCenter_WithSlope()
    {
        // 身体系中心の名声連動（案D・設計書01 §1.1b-2 但し書き）: talentCenterがDefaultのとき PhysicalMean のまま、
        // 中心が上がったぶんの slope 倍だけ上がる。slope=0 なら旧来の名声独立に戻る（1ビット一致の口）。
        // このテストは TalentCenterFromFame の形を直接参照しない（talentCenterを手で与える）＝写像の再校正(#1031)で変わらない。
        Assert.Equal(C.PhysicalMean, C.PhysicalCenterFor(C.TalentCenterDefault), 9);
        Assert.Equal(C.PhysicalMean + 40 * C.PhysicalTalentSlope, C.PhysicalCenterFor(C.TalentCenterDefault + 40), 9);
        var independent = C with { PhysicalTalentSlope = 0 };
        Assert.Equal(independent.PhysicalMean, independent.PhysicalCenterFor(90), 9);
    }

    [Fact]
    public void TalentCenter_DiminishingReturns_MarginalGainShrinks()
    {
        // 逓減カーブ（線形→漸近）: 名声が上がるほど、同じ+25の伸びに対するtalentCenter増分は小さくなる。
        var gainLow = C.TalentCenterFromFame(57) - C.TalentCenterFromFame(32);
        var gainHigh = C.TalentCenterFromFame(100) - C.TalentCenterFromFame(75);
        Assert.True(gainHigh < gainLow, $"逓減していない（低名声帯+{gainLow:F2} vs 高名声帯+{gainHigh:F2}）");
    }

    [Fact]
    public void TalentCenter_NeverExceedsCap_AboveFameRange()
    {
        // Fameは仕様上0-100だが、式自体もFameの想定域を超えて暴走しないことを確認（安全側）。
        var beyond = C.TalentCenterFromFame(1000);
        Assert.True(beyond <= C.FameTalentCenterCap + 1e-9);
    }
}

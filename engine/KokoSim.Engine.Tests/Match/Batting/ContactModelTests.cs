using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>
/// コンタクト判定（設計書15 Phase E-2）の物理妥当性テスト。誘発変化合成量が空振り率を単調に上げること、
/// 無回転球相当（変化量≈0）が帯の最低水準になることを固定する。
/// </summary>
public sealed class ContactModelTests
{
    private static PitchPlan Plan(double velocityKmh = 145.0, double stuff = 0.0) => new()
    {
        Type = PitchType.Fastball,
        AimX = 0.0,
        AimY = 0.5,
        VelocityKmh = velocityKmh,
        Stuff = stuff,
    };

    [Fact]
    public void Resolve_TwoStrikeFoulBonus_RaisesFoulShare_OnlyAtTwoOrMoreStrikes()
    {
        // issue #683: 追い込まれたら当てにいってファールで粘る。2ストライク未満は FoulShare のまま。
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients { FoulShare = 0.30, TwoStrikeFoulBonus = 0.30, WhiffIntercept = -30.0 };
        var features = new PitchTrajectoryFeatures(0.0, 0.0, 0.45);
        double FoulRate(int strikes)
        {
            var rng = new Xoshiro256Random(7);
            var fouls = 0;
            const int n = 20000;
            for (var i = 0; i < n; i++)
                if (ContactModel.Resolve(batter, Plan(), features, true, coeff, rng, 0, strikes).Outcome == ContactOutcome.Foul) fouls++;
            return (double)fouls / n;
        }

        Assert.InRange(FoulRate(0), 0.28, 0.32);
        Assert.InRange(FoulRate(1), 0.28, 0.32);
        Assert.InRange(FoulRate(2), 0.58, 0.62);
    }

    [Fact]
    public void WhiffProbability_IncreasesMonotonically_WithBreakMagnitude()
    {
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var plan = Plan();

        var magnitudes = new[] { 0.0, 0.1, 0.2, 0.3, 0.4, 0.6, 0.8 };
        double? previous = null;
        foreach (var m in magnitudes)
        {
            var features = new PitchTrajectoryFeatures(m, 0.0, 0.45);
            var p = ContactModel.WhiffProbability(batter, plan, features, inZone: true, coeff);
            if (previous is not null)
            {
                Assert.True(p > previous, $"magnitude={m}: {p} should exceed previous {previous}");
            }
            previous = p;
        }
    }

    [Fact]
    public void WhiffProbability_AtZeroBreak_IsTheFloorOfTheCurve()
    {
        // 無回転球（誘発変化≈0）は帯の中で最も空振りしにくい水準になるはず。
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var plan = Plan();

        var zero = new PitchTrajectoryFeatures(0.0, 0.0, 0.45);
        var pZero = ContactModel.WhiffProbability(batter, plan, zero, inZone: true, coeff);

        foreach (var m in new[] { 0.1, 0.3, 0.5, 0.8 })
        {
            var features = new PitchTrajectoryFeatures(m, 0.0, 0.45);
            var p = ContactModel.WhiffProbability(batter, plan, features, inZone: true, coeff);
            Assert.True(p > pZero, $"breakMagnitude={m} の空振り率が無回転相当を上回らなければならない");
        }
    }

    [Fact]
    public void WhiffProbability_UsesCombinedVerticalAndHorizontalBreak()
    {
        // 縦横合成（設計書15 Phase E-2, ユーザー承認）: 縦だけ0.3mと横だけ0.3mは合成量が同じなので同確率。
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var plan = Plan();

        var verticalOnly = new PitchTrajectoryFeatures(0.3, 0.0, 0.45);
        var horizontalOnly = new PitchTrajectoryFeatures(0.0, 0.3, 0.45);

        var pVertical = ContactModel.WhiffProbability(batter, plan, verticalOnly, inZone: true, coeff);
        var pHorizontal = ContactModel.WhiffProbability(batter, plan, horizontalOnly, inZone: true, coeff);

        Assert.Equal(pVertical, pHorizontal, 9);
    }

    // ===== 球速の物理層接続（issue #14 ①: 速い球ほど空振りが取れる） =====

    [Fact]
    public void Stuff_FromVelocity_WeighsVelocityLevelTwiceContactLevel()
    {
        // 球速1レベルの空振り対数オッズ寄与が、ミート1レベルの約2倍になっていること。
        // 経緯: 旧モデルは 0.0094 対 0.030（球速1Lv＝ミート0.31Lv）→ issue #14 で等価（1.0倍）→
        // issue #578 Step 2 で **2倍**（オーナー方針 Q24-1「球速のバリューはもう少し高くてよい」）。
        // 校正前は「制球（与四球）の失点価値は球速の約4倍」だった＝2倍にして差を半分に詰める。
        // 平均三振率は whiff_intercept（−2.40→−2.52）で据え置き＝動くのは球速の**配分**だけ（帯不変）。
        // Lv50 を境に上位の傾きが 0.47→0.36 に寝る（issue velocity-kmh-mapping-flatten-top, 2026-08-18）ので、
        // 「2倍」は Lv≦50 側の傾きで測る。上位側は 0.36/0.47 倍＝約1.5倍（物理は km/h のまま・意図した副作用）。
        var coeff = new BattingCoefficients();
        var perVelocityLevelBelow =
            (PitcherAttributes.VelocityKmhFromLevel(50) - PitcherAttributes.VelocityKmhFromLevel(49))
            * coeff.StuffPerKmh;
        Assert.InRange(perVelocityLevelBelow / coeff.WhiffContactSlope, 1.8, 2.25);

        var perVelocityLevelAbove =
            (PitcherAttributes.VelocityKmhFromLevel(51) - PitcherAttributes.VelocityKmhFromLevel(50))
            * coeff.StuffPerKmh;
        Assert.InRange(perVelocityLevelAbove / coeff.WhiffContactSlope, 1.4, 1.7);
    }

    [Fact]
    public void Stuff_IsZero_ForTheLeagueAveragePitcher()
    {
        // 基準球速はリーグ平均投手の平均リリース球速（最速132 − 平均ドロップ4）。ここで寄与0＝帯中立。
        var coeff = new BattingCoefficients();
        var pitching = new PitchingCoefficients();
        var meanRelease = PitcherAttributes.LeagueAverage.MaxVelocityKmh - pitching.VelocityDropMeanKmh;

        Assert.Equal(meanRelease, coeff.StuffBaseVelocityKmh, 1);
    }

    [Fact]
    public void WhiffProbability_IncreasesMonotonically_WithPitchVelocity()
    {
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var features = new PitchTrajectoryFeatures(0.47, 0.0, 0.45);

        double? previous = null;
        foreach (var level in new[] { 30, 45, 60, 75, 90 })
        {
            var velocity = PitcherAttributes.VelocityKmhFromLevel(level);
            var stuff = (velocity - coeff.StuffBaseVelocityKmh) * coeff.StuffPerKmh;
            var p = ContactModel.WhiffProbability(
                batter, Plan(velocity, stuff), features, inZone: true, coeff);
            if (previous is not null) Assert.True(p > previous, $"球速Lv{level} で空振り率が上がっていない");
            previous = p;
        }
    }

    // ===== 打球質への投手側寄与（issue #14 ②: 技巧派は打たせて取る） =====

    [Fact]
    public void PitcherContactQualityBias_IsIdentity_ForTheLeagueAveragePitcher()
    {
        // 基準球速・制球50・基準変化量では寄与0＝係数を入れても得点環境の平均は動かない。
        var coeff = new BattingCoefficients();
        var plan = Plan(coeff.ContactQualityVelocityRefKmh) with { ControlLevel = 50.0 };
        var features = new PitchTrajectoryFeatures(coeff.ContactQualityBreakRefM, 0.0, 0.45);

        Assert.Equal(0.0, ContactModel.PitcherContactQualityBias(plan, features, coeff), 9);
    }

    [Fact]
    public void PitcherContactQualityBias_ControlAndBreak_WeakenContact()
    {
        var coeff = new BattingCoefficients();
        var features = new PitchTrajectoryFeatures(coeff.ContactQualityBreakRefM, 0.0, 0.45);
        var neutral = Plan(coeff.ContactQualityVelocityRefKmh) with { ControlLevel = 50.0 };

        // 制球が良いほど芯を外させる。
        var precise = neutral with { ControlLevel = 80.0 };
        Assert.True(ContactModel.PitcherContactQualityBias(precise, features, coeff)
            < ContactModel.PitcherContactQualityBias(neutral, features, coeff));

        // 変化量が大きいほど詰まる。
        var sharp = new PitchTrajectoryFeatures(coeff.ContactQualityBreakRefM + 0.15, 0.0, 0.45);
        Assert.True(ContactModel.PitcherContactQualityBias(neutral, sharp, coeff)
            < ContactModel.PitcherContactQualityBias(neutral, features, coeff));
    }

    [Fact]
    public void PitcherContactQualityBias_FasterPitches_AreHitWeaker_AndUseReactionVelocity()
    {
        // 速い球は空振りを取れるうえ、当てられても芯を外しやすい（contact_quality_per_kmh < 0。設計書21・2026-08-19）。
        // 旧 +0.003（速いほど強い打球）は「速い球ほど絶対変化量が小さい」効果と合わさって球速の価値を打ち消し、
        // 球速+40Lv で打球あたり安打率が .323→.376 と**速いほど打たれる**倒錯を生んでいた（球速の価値の是正）。
        var coeff = new BattingCoefficients();
        Assert.True(coeff.ContactQualityPerKmh < 0.0);
        var features = new PitchTrajectoryFeatures(coeff.ContactQualityBreakRefM, 0.0, 0.45);
        var slow = Plan(coeff.ContactQualityVelocityRefKmh - 10) with { ControlLevel = 50.0 };
        var fast = Plan(coeff.ContactQualityVelocityRefKmh + 10) with { ControlLevel = 50.0 };

        Assert.True(ContactModel.PitcherContactQualityBias(fast, features, coeff)
            < ContactModel.PitcherContactQualityBias(slow, features, coeff));

        // 読むのは反応球速（効きを通した値）: 実球速が速くても反応球速が基準なら寄与0。
        var muted = fast with { ReactionVelocityKmh = coeff.ContactQualityVelocityRefKmh };
        Assert.Equal(0.0, ContactModel.PitcherContactQualityBias(muted, features, coeff), 12);
    }

    [Fact]
    public void Resolve_WeakContactPitcher_ProducesSlowerBattedBalls()
    {
        // 「打たせて取る」の土台: 同じ打者でも制球とキレのある投手からは平均打球初速が落ちる。
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var sharp = new PitchTrajectoryFeatures(coeff.ContactQualityBreakRefM + 0.12, 0.0, 0.50);
        var plainFeatures = new PitchTrajectoryFeatures(coeff.ContactQualityBreakRefM, 0.0, 0.50);

        double MeanExit(PitchPlan plan, PitchTrajectoryFeatures features)
        {
            double sum = 0; var n = 0;
            for (var i = 0; i < 4000; i++)
            {
                var rng = new Xoshiro256Random((ulong)(i + 1));
                var (outcome, ball) = ContactModel.Resolve(batter, plan, features, inZone: true, coeff, rng);
                if (outcome != ContactOutcome.InPlay || ball is null) continue;
                sum += ball.ExitVelocityMps;
                n++;
            }
            return sum / n;
        }

        var neutral = Plan(coeff.ContactQualityVelocityRefKmh) with { ControlLevel = 50.0 };
        var crafty = Plan(coeff.ContactQualityVelocityRefKmh) with { ControlLevel = 85.0 };

        Assert.True(MeanExit(crafty, sharp) < MeanExit(neutral, plainFeatures));
    }

    [Fact]
    public void WhiffProbability_OutOfZone_IsHarderToMakeContactWith()
    {
        var batter = BatterAttributes.LeagueAverage;
        var coeff = new BattingCoefficients();
        var plan = Plan();
        var features = new PitchTrajectoryFeatures(0.3, 0.0, 0.45);

        var inZone = ContactModel.WhiffProbability(batter, plan, features, inZone: true, coeff);
        var outOfZone = ContactModel.WhiffProbability(batter, plan, features, inZone: false, coeff);

        Assert.True(outOfZone > inZone);
    }
}

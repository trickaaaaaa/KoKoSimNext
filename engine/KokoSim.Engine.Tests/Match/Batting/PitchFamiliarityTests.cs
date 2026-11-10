using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Batting;

/// <summary>球種の慣れ（設計書02 §2.3b 案A・issue #836）。打者×投手×球種の試合内蓄積と、その効きを見張る。</summary>
public sealed class PitchFamiliarityTests
{
    private static readonly BattingCoefficients C = new();
    private static readonly BatterAttributes Batter = new() { Contact = 50, Discipline = 50 };

    private static PitchPlan PlanOf(PitchType type, int rank = 50) => new()
    {
        Type = type, AimX = 0, AimY = 0.6, VelocityKmh = 140, Stuff = 0.5, ControlLevel = 60, PitchRank = rank,
    };

    private static PitchTrajectoryFeatures Feat(double breakM) => new(breakM, 0.0, 0.42);

    // ── PitchFamiliarityTracker（状態機械） ──

    [Fact]
    public void Advance_AccumulatesTheThrownType_AndDecaysOthers()
    {
        var t = new PitchFamiliarityTracker();
        Assert.Equal(0.0, t.Level(PitchType.Fastball));

        t.Advance(PitchType.Fastball, C.PitchFamiliarityDecay);
        Assert.Equal(1.0, t.Level(PitchType.Fastball), 12);

        t.Advance(PitchType.Fastball, C.PitchFamiliarityDecay);
        // 2球目: λ×1 + 1 = 1.85（λ=0.85既定）
        Assert.Equal(C.PitchFamiliarityDecay * 1.0 + 1.0, t.Level(PitchType.Fastball), 12);

        var beforeSlider = t.Level(PitchType.Fastball);
        t.Advance(PitchType.Slider, C.PitchFamiliarityDecay);
        // 違う球種を投げるとストレートの見慣れ度は減衰だけ（加算なし）、スライダーは1から始まる。
        Assert.Equal(beforeSlider * C.PitchFamiliarityDecay, t.Level(PitchType.Fastball), 12);
        Assert.Equal(1.0, t.Level(PitchType.Slider), 12);
    }

    [Fact]
    public void Advance_RepeatedSameType_ConvergesToSteadyState()
    {
        // 同じ球種を何百球も続けると 1/(1-λ) に収束する（幾何級数の定常値）。
        var t = new PitchFamiliarityTracker();
        for (var i = 0; i < 500; i++) t.Advance(PitchType.Fastball, C.PitchFamiliarityDecay);
        var steady = 1.0 / (1.0 - C.PitchFamiliarityDecay);
        Assert.Equal(steady, t.Level(PitchType.Fastball), 6);
    }

    // ── ContactModel.PitchFamiliarityEffect（純関数） ──

    [Fact]
    public void Effect_IsZero_WhenLevelIsNull()
    {
        Assert.Equal(0.0, ContactModel.PitchFamiliarityEffect(null, 50, C));
    }

    [Fact]
    public void Effect_IsPositive_AboveBaseline_AndNegative_BelowBaseline()
    {
        var above = ContactModel.PitchFamiliarityEffect(C.PitchFamiliarityBaselineLevel + 1.0, 50, C);
        var below = ContactModel.PitchFamiliarityEffect(C.PitchFamiliarityBaselineLevel - 1.0, 50, C);
        var at = ContactModel.PitchFamiliarityEffect(C.PitchFamiliarityBaselineLevel, 50, C);
        Assert.True(above > 0.0);
        Assert.True(below < 0.0);
        Assert.Equal(0.0, at, 12);
    }

    [Fact]
    public void Effect_IsDiscounted_ByHighPitchRank()
    {
        // キレ・球威（球種ランク）が高い球ほど、慣れられても効きが打ち消される（§2.3b「慣れを万能にしない」）。
        var level = C.PitchFamiliarityBaselineLevel + 2.0;
        var atAverageRank = ContactModel.PitchFamiliarityEffect(level, 50, C);
        var atHighRank = ContactModel.PitchFamiliarityEffect(level, 90, C);
        Assert.True(atHighRank < atAverageRank);
        Assert.True(atHighRank >= 0.0);   // 下限0（割引で逆転しない）
    }

    // ── 空振り確率への配線 ──

    [Fact]
    public void WhiffProbability_DropsAsPitchFamiliarityLevelRises()
    {
        var plan = PlanOf(PitchType.Fastball);
        var low = ContactModel.WhiffProbability(Batter, plan, Feat(0.1), true, C, pitchFamiliarityLevel: 0.0);
        var high = ContactModel.WhiffProbability(Batter, plan, Feat(0.1), true, C, pitchFamiliarityLevel: C.PitchFamiliarityBaselineLevel + 4.0);
        Assert.True(high < low, $"見慣れ度が上がっても空振りにくくなっていない: {low:F3} → {high:F3}");
    }

    [Fact]
    public void SameTypeThrownRepeatedly_GetsEasierToHit_PitchOverPitch()
    {
        // 「同じ球種を続けるとその球種が打たれやすくなる」（issue #836 完了条件）を、
        // トラッカー→空振り確率の経路で直接確かめる。毎球 Level→WhiffProbability→Advance の順（AtBatSession と同じ順）。
        var tracker = new PitchFamiliarityTracker();
        var plan = PlanOf(PitchType.Fastball);
        var prevWhiff = double.MaxValue;
        for (var i = 0; i < 6; i++)
        {
            var level = tracker.Level(PitchType.Fastball);
            var whiff = ContactModel.WhiffProbability(Batter, plan, Feat(0.1), true, C, pitchFamiliarityLevel: level);
            if (i > 0) Assert.True(whiff <= prevWhiff, $"{i}球目で空振り率が上がった: {prevWhiff:F4} → {whiff:F4}");
            prevWhiff = whiff;
            tracker.Advance(PitchType.Fastball, C.PitchFamiliarityDecay);
        }
    }

    [Fact]
    public void Quality_RisesAsPitchFamiliarityLevelRises()
    {
        var plan = PlanOf(PitchType.Fastball);
        var feat = Feat(0.1);
        var lowBias = ContactModel.PitcherContactQualityBias(plan, feat, C)
            + C.PitchFamiliarityQualityPerLevel * ContactModel.PitchFamiliarityEffect(0.0, plan.PitchRank, C);
        var highBias = ContactModel.PitcherContactQualityBias(plan, feat, C)
            + C.PitchFamiliarityQualityPerLevel * ContactModel.PitchFamiliarityEffect(C.PitchFamiliarityBaselineLevel + 4.0, plan.PitchRank, C);
        Assert.True(highBias > lowBias);
    }

    // ── 継投リセット（TeamState） ──

    [Fact]
    public void TeamState_PitchFamiliarity_ResetsForANewPitcher()
    {
        var team = DeterminismCards.Team("A");
        var state = new TeamState(team);
        var batter = team.BattingOrder[0];
        var p1 = new Player { Name = "P1", Position = KokoSim.Engine.Match.Field.FieldPosition.Pitcher };
        var p2 = new Player { Name = "P2", Position = KokoSim.Engine.Match.Field.FieldPosition.Pitcher };

        var t1 = state.PitchFamiliarityTrackerFor(batter, p1);
        t1.Advance(PitchType.Fastball, C.PitchFamiliarityDecay);
        t1.Advance(PitchType.Fastball, C.PitchFamiliarityDecay);
        Assert.True(t1.Level(PitchType.Fastball) > 0.0);

        // 同じ投手に対しては同じインスタンス（打席をまたいで持続）。
        Assert.Same(t1, state.PitchFamiliarityTrackerFor(batter, p1));

        // 投手が変われば別インスタンス＝0から（継投でリセット）。
        var t2 = state.PitchFamiliarityTrackerFor(batter, p2);
        Assert.NotSame(t1, t2);
        Assert.Equal(0.0, t2.Level(PitchType.Fastball));
    }
}

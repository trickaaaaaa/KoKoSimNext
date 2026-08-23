using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using NationState = KokoSim.Engine.Nation.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 監督の転任判定（設計書04 §1.3、issue「監督の転任システムが実ゲームに完全未配線」）。
/// フリー化資格・残留ロジスティック・強制転任の各分岐と、実在校からの候補選定を担保する。
/// </summary>
public sealed class ManagerTransferModelTests
{
    private static readonly SchoolNameVocab Vocab = new();
    private static readonly NationCoefficients NationCoeff = new();
    private static readonly CareerCoefficients C = new();

    private static NationState BuildNation(ulong seed = 2026)
        => NationGenerator.Generate(Vocab, NationCoeff, new Xoshiro256Random(seed));

    [Fact]
    public void Evaluate_Teacher_BecomesFreeAgent_WhenKoshienThresholdReached()
    {
        var m = new Manager { Status = ManagerStatus.Teacher, KoshienAppearances = 1, Fame = 10 };
        var nation = BuildNation();

        var result = ManagerTransferModel.Evaluate(m, currentPrefectureId: 13, nation, new Xoshiro256Random(1), C);

        Assert.Equal(TransferEventKind.BecameFreeAgent, result.Kind);
        Assert.True(result.CanStay);
        Assert.Equal(C.FreeAgentOfferCount, result.Candidates.Count);
    }

    [Fact]
    public void Evaluate_Teacher_BecomesFreeAgent_WhenFameThresholdReached()
    {
        var m = new Manager { Status = ManagerStatus.Teacher, KoshienAppearances = 0, Fame = C.FreeFameThreshold };
        var nation = BuildNation();

        var result = ManagerTransferModel.Evaluate(m, currentPrefectureId: 13, nation, new Xoshiro256Random(1), C);

        Assert.Equal(TransferEventKind.BecameFreeAgent, result.Kind);
    }

    [Fact]
    public void Evaluate_Teacher_BelowThreshold_HighTrust_RetainsWithoutTransfer()
    {
        // 信頼度が十分高いロジスティック中点+3σ相当なら、残留確率は上限(0.8)近傍に張り付く。
        var m = new Manager { Status = ManagerStatus.Teacher, Trust = C.RetainTrustThreshold + 3 * C.RetainTrustSpread };
        var nation = BuildNation();

        // 上限0.8を下回る一様乱数(0.1)なら必ず残留する。
        var rng = new FixedDoubleRandomSource(0.1);
        var result = ManagerTransferModel.Evaluate(m, currentPrefectureId: 13, nation, rng, C);

        Assert.Equal(TransferResult.Stayed, result);
    }

    [Fact]
    public void Evaluate_Teacher_BelowThreshold_LowTrust_ForcedTransfer_HasExactlyOneCandidate_NoStayOption()
    {
        // 信頼度が中点より十分低ければ残留確率はほぼ0。0.99は必ず残留確率を上回る。
        var m = new Manager { Status = ManagerStatus.Teacher, Trust = C.RetainTrustThreshold - 3 * C.RetainTrustSpread };
        var nation = BuildNation();

        var rng = new FixedDoubleRandomSource(0.99);
        var result = ManagerTransferModel.Evaluate(m, currentPrefectureId: 13, nation, rng, C);

        Assert.Equal(TransferEventKind.ForcedTransfer, result.Kind);
        Assert.False(result.CanStay);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Evaluate_ForcedTransfer_NeverPicksCurrentPrefecture()
    {
        var m = new Manager { Status = ManagerStatus.Teacher, Trust = 0 };
        var nation = BuildNation();

        for (var seed = 0UL; seed < 20; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var result = ManagerTransferModel.Evaluate(m, currentPrefectureId: 13, nation, rng, C);
            if (result.Kind != TransferEventKind.ForcedTransfer) continue;
            Assert.All(result.Candidates, cand => Assert.NotEqual(13, cand.PrefectureId));
        }
    }

    [Fact]
    public void Evaluate_FreeAgent_OfferChanceRoll_GatesWhetherOffersAppear()
    {
        var m = new Manager { Status = ManagerStatus.Free };
        var nation = BuildNation();

        var offered = ManagerTransferModel.Evaluate(
            m, 13, nation, new FixedDoubleRandomSource(C.FreeAgentOfferChance - 0.01), C);
        Assert.Equal(TransferEventKind.FreeAgentOffer, offered.Kind);
        Assert.True(offered.CanStay);
        Assert.Equal(C.FreeAgentOfferCount, offered.Candidates.Count);

        var notOffered = ManagerTransferModel.Evaluate(
            m, 13, nation, new FixedDoubleRandomSource(C.FreeAgentOfferChance + 0.01), C);
        Assert.Equal(TransferResult.Stayed, notOffered);
    }

    [Fact]
    public void Evaluate_Candidates_AreDistinctRealSchools()
    {
        var m = new Manager { Status = ManagerStatus.Free };
        var nation = BuildNation();

        var result = ManagerTransferModel.Evaluate(
            m, 13, nation, new FixedDoubleRandomSource(0.0), C);

        var ids = result.Candidates.Select(cand => cand.SchoolId).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        foreach (var cand in result.Candidates)
            Assert.Contains(nation.Schools, s => s.Id == cand.SchoolId && s.Name == cand.SchoolName);
    }

    [Fact]
    public void Evaluate_IsDeterministic_ForSameSeed()
    {
        TransferResult Run()
        {
            var m = new Manager { Status = ManagerStatus.Free };
            var nation = BuildNation();
            return ManagerTransferModel.Evaluate(m, 13, nation, new Xoshiro256Random(777), C);
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ApplyTransfer_ResetsTrustOnly_KeepingFameAndCoaching()
    {
        var m = new Manager { Trust = 90, Fame = 55, CoachingBatting = 40 };

        ManagerTransferModel.ApplyTransfer(m, C);

        Assert.Equal(C.TrustReset, m.Trust);
        Assert.Equal(55, m.Fame);
        Assert.Equal(40, m.CoachingBatting);
    }

    /// <summary>常に同じ値を返す <see cref="IRandomSource"/>（NextDouble のしきい値をまたぐテスト専用）。</summary>
    private sealed class FixedDoubleRandomSource : IRandomSource
    {
        private readonly double _value;
        private readonly IRandomSource _inner = new Xoshiro256Random(1);
        public FixedDoubleRandomSource(double value) => _value = value;
        public ulong NextUInt64() => _inner.NextUInt64();
        public double NextDouble() => _value;
        public int NextInt(int minInclusive, int maxExclusive) => _inner.NextInt(minInclusive, maxExclusive);
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) => _inner.NextGaussian(mean, stdDev);
        public IRandomSource Fork(ulong streamId) => _inner.Fork(streamId);
    }
}

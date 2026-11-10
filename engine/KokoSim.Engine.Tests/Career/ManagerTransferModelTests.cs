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
        // 最低在任年数は満たしている前提（下限そのものの検証は別テスト）。
        var m = new Manager
        {
            Status = ManagerStatus.Teacher,
            Trust = C.RetainTrustThreshold + 3 * C.RetainTrustSpread,
            YearsAtSchool = C.MinTenureYears,
        };
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
        // 最低在任年数は満たしている前提。
        var m = new Manager
        {
            Status = ManagerStatus.Teacher,
            Trust = C.RetainTrustThreshold - 3 * C.RetainTrustSpread,
            YearsAtSchool = C.MinTenureYears,
        };
        var nation = BuildNation();

        var rng = new FixedDoubleRandomSource(0.99);
        var result = ManagerTransferModel.Evaluate(m, currentPrefectureId: 13, nation, rng, C);

        Assert.Equal(TransferEventKind.ForcedTransfer, result.Kind);
        Assert.False(result.CanStay);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Evaluate_ForcedTransfer_AlwaysStaysWithinCurrentPrefecture()
    {
        // 教員は都道府県教育委員会の異動＝強制転任は県境を越えない。最低在任年数は満たしている前提。
        var m = new Manager { Status = ManagerStatus.Teacher, Trust = 0, YearsAtSchool = C.MinTenureYears };
        var nation = BuildNation();

        var sawForcedTransfer = false;
        for (var seed = 0UL; seed < 20; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var result = ManagerTransferModel.Evaluate(m, currentPrefectureId: 13, nation, rng, C);
            if (result.Kind != TransferEventKind.ForcedTransfer) continue;
            sawForcedTransfer = true;
            Assert.All(result.Candidates, cand => Assert.Equal(13, cand.PrefectureId));
        }
        Assert.True(sawForcedTransfer, "20シードのどれかは強制転任に当たる想定（テストの前提確認）");
    }

    [Fact]
    public void Evaluate_BelowMinTenure_NeverForcedTransfer_EvenWithZeroTrust()
    {
        // 赴任したて（在任0年）は信頼度が最低でも強制転任の抽選そのものに入らない＝必ず残留。
        // 「採った1年生が卒業するまでは追い出されない」という下限（ユーザー指摘で追加）。
        var m = new Manager { Status = ManagerStatus.Teacher, Trust = 0, YearsAtSchool = 0 };
        var nation = BuildNation();

        for (var seed = 0UL; seed < 20; seed++)
        {
            var result = ManagerTransferModel.Evaluate(m, currentPrefectureId: 13, nation, new Xoshiro256Random(seed), C);
            Assert.Equal(TransferResult.Stayed, result);
        }

        // 在任年数が下限ちょうど手前(MinTenureYears-1)でもまだ対象外。
        var almost = new Manager { Status = ManagerStatus.Teacher, Trust = 0, YearsAtSchool = C.MinTenureYears - 1 };
        var stillStayed = ManagerTransferModel.Evaluate(almost, 13, nation, new FixedDoubleRandomSource(0.99), C);
        Assert.Equal(TransferResult.Stayed, stillStayed);
    }

    [Fact]
    public void Evaluate_FreeAgentOffers_NeverPickCurrentPrefecture()
    {
        // フリー監督（本拠地選択・以降のオファー）は教員身分を離れた全国区＝県境の制約を受けない。
        var becameFree = new Manager { Status = ManagerStatus.Teacher, KoshienAppearances = 1 };
        var nation = BuildNation();
        var becameFreeResult = ManagerTransferModel.Evaluate(
            becameFree, currentPrefectureId: 13, nation, new Xoshiro256Random(1), C);
        Assert.Equal(TransferEventKind.BecameFreeAgent, becameFreeResult.Kind);
        Assert.All(becameFreeResult.Candidates, cand => Assert.NotEqual(13, cand.PrefectureId));

        var alreadyFree = new Manager { Status = ManagerStatus.Free };
        var offerResult = ManagerTransferModel.Evaluate(
            alreadyFree, 13, nation, new FixedDoubleRandomSource(0.0), C);
        Assert.Equal(TransferEventKind.FreeAgentOffer, offerResult.Kind);
        Assert.All(offerResult.Candidates, cand => Assert.NotEqual(13, cand.PrefectureId));
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
    public void ApplyTransfer_ResetsTrustAndTenure_KeepingFameAndCoaching()
    {
        var m = new Manager { Trust = 90, Fame = 55, CoachingBatting = 40, YearsAtSchool = 7 };

        ManagerTransferModel.ApplyTransfer(m, C);

        Assert.Equal(C.TrustReset, m.Trust);
        Assert.Equal(0, m.YearsAtSchool);
        Assert.Equal(55, m.Fame);
        Assert.Equal(40, m.CoachingBatting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyDecision_BecameFreeAgent_MakesManagerFree_WhetherStayingOrMoving(bool transferred)
    {
        // issue #777: 昇格を確定したら残留でも転任でも身分がフリーになり、翌年はフリー化資格を再び返さない。
        var m = new Manager { Status = ManagerStatus.Teacher, KoshienAppearances = 1, Trust = 80, YearsAtSchool = 4 };
        var nation = BuildNation();
        var first = ManagerTransferModel.Evaluate(m, 13, nation, new Xoshiro256Random(1), C);
        Assert.Equal(TransferEventKind.BecameFreeAgent, first.Kind);

        ManagerTransferModel.ApplyDecision(m, first.Kind, transferred, C);

        Assert.Equal(ManagerStatus.Free, m.Status);
        Assert.Equal(transferred ? C.TrustReset : 80, m.Trust);
        Assert.Equal(transferred ? 0 : 4, m.YearsAtSchool);
        for (ulong seed = 0; seed < 50; seed++)
        {
            var next = ManagerTransferModel.Evaluate(m, 13, nation, new Xoshiro256Random(seed), C);
            Assert.NotEqual(TransferEventKind.BecameFreeAgent, next.Kind);
            Assert.NotEqual(TransferEventKind.ForcedTransfer, next.Kind);
        }
    }

    [Fact]
    public void ApplyDecision_ForcedTransfer_KeepsTeacherStatus()
    {
        var m = new Manager { Status = ManagerStatus.Teacher, Trust = 70, YearsAtSchool = 5 };

        ManagerTransferModel.ApplyDecision(m, TransferEventKind.ForcedTransfer, transferred: true, C);

        Assert.Equal(ManagerStatus.Teacher, m.Status);
        Assert.Equal(C.TrustReset, m.Trust);
        Assert.Equal(0, m.YearsAtSchool);
    }

    [Fact]
    public void ApplyDecision_FreeAgentOffer_Staying_ChangesNothing()
    {
        var m = new Manager { Status = ManagerStatus.Free, Trust = 70, YearsAtSchool = 5 };

        ManagerTransferModel.ApplyDecision(m, TransferEventKind.FreeAgentOffer, transferred: false, C);

        Assert.Equal(ManagerStatus.Free, m.Status);
        Assert.Equal(70, m.Trust);
        Assert.Equal(5, m.YearsAtSchool);
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

    [Fact]
    public void OfferCandidateCount_LowFame_StaysAtFloor()
    {
        Assert.Equal(C.FreeAgentOfferCount, ManagerTransferModel.OfferCandidateCount(0, C));
        Assert.Equal(C.FreeAgentOfferCount, ManagerTransferModel.OfferCandidateCount(C.FreeAgentOfferFameBase, C));
        Assert.Equal(C.FreeAgentOfferCount, ManagerTransferModel.OfferCandidateCount(C.FreeAgentOfferFameBase - 20, C));
    }

    [Fact]
    public void OfferCandidateCount_RisesMonotonicallyWithFame_AndClampsAtCeiling()
    {
        var prev = 0;
        for (var fame = 0.0; fame <= 100.0; fame += 0.5)
        {
            var n = ManagerTransferModel.OfferCandidateCount(fame, C);
            Assert.InRange(n, C.FreeAgentOfferCount, C.FreeAgentOfferMaxCount);
            Assert.True(n >= prev, $"fame {fame}: {n} < {prev}");
            prev = n;
        }
        Assert.Equal(C.FreeAgentOfferMaxCount, ManagerTransferModel.OfferCandidateCount(1000, C));
    }

    [Fact]
    public void OfferCandidateCount_FormulaExamples()
    {
        Assert.Equal(2, ManagerTransferModel.OfferCandidateCount(50, C));
        Assert.Equal(7, ManagerTransferModel.OfferCandidateCount(70, C));
        Assert.Equal(9, ManagerTransferModel.OfferCandidateCount(81.9, C));
        Assert.Equal(10, ManagerTransferModel.OfferCandidateCount(82, C));
    }

    [Theory]
    [InlineData(TransferEventKind.BecameFreeAgent)]
    [InlineData(TransferEventKind.FreeAgentOffer)]
    public void Evaluate_HighFame_ListsMoreDistinctOutOfPrefectureSchools(TransferEventKind kind)
    {
        var nation = BuildNation();
        Manager Make(double fame) => kind == TransferEventKind.BecameFreeAgent
            ? new Manager { Status = ManagerStatus.Teacher, KoshienAppearances = 1, Fame = fame }
            : new Manager { Status = ManagerStatus.Free, Fame = fame };
        // FreeAgentOffer は最初の NextDouble がオファー確率を下回る種でないと届かない＝その種を探す。
        ulong seed = 1;
        while (kind == TransferEventKind.FreeAgentOffer && new Xoshiro256Random(seed).NextDouble() >= C.FreeAgentOfferChance)
            seed++;
        var low = ManagerTransferModel.Evaluate(Make(10), 13, nation, new Xoshiro256Random(seed), C);
        var high = ManagerTransferModel.Evaluate(Make(95), 13, nation, new Xoshiro256Random(seed), C);

        Assert.Equal(kind, low.Kind);
        Assert.Equal(kind, high.Kind);
        Assert.Equal(C.FreeAgentOfferCount, low.Candidates.Count);
        Assert.Equal(C.FreeAgentOfferMaxCount, high.Candidates.Count);
        Assert.Equal(high.Candidates.Count, high.Candidates.Select(x => x.SchoolId).Distinct().Count());
        Assert.All(high.Candidates, x => Assert.NotEqual(13, x.PrefectureId));
        // 引き順は変わらない＝名声が低いときの候補は、名声が高いときの先頭と同じ学校。
        Assert.Equal(
            low.Candidates.Select(x => x.SchoolId),
            high.Candidates.Take(low.Candidates.Count).Select(x => x.SchoolId));
    }
}

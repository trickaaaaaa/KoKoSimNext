using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// スカウト（設計書04 §2・issue #867）。枠数は年契約「スカウト網」の段から、候補生成・入部成否の
/// 単調性、受動枠を1ビットも動かさないこと、を固定する。旧 <c>RecruitingTests</c>（issue #588）を置き換える。
/// </summary>
public sealed class ScoutingTests
{
    private static readonly RosterCoefficients Roster = new();
    private static readonly ScoutCoefficients C = new();
    private static readonly TeamStrengthCoefficients Strength = new();
    private static readonly ContractCatalog Contracts = ContractCatalog.Default;

    // ── 枠の解禁（スカウト網の契約段） ──────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void SlotsForLevel_MatchesContractTiers(int level, int expectedSlots)
        => Assert.Equal(expectedSlots, ScoutModel.SlotsForLevel(level, Contracts));

    // ── 距離 ────────────────────────────────────────────────

    [Theory]
    [InlineData(13, 13, RecruitDistance.SamePrefecture)]  // 神奈川 → 神奈川
    [InlineData(13, 12, RecruitDistance.SameRegion)]      // 神奈川 → 東京（関東）
    [InlineData(13, 21, RecruitDistance.NeighborRegion)]  // 神奈川 → 静岡（中部＝関東の隣）
    [InlineData(13, 46, RecruitDistance.Far)]             // 神奈川 → 沖縄（九州）
    [InlineData(0, 13, RecruitDistance.NeighborRegion)]   // 北海道 → 神奈川（北海道東北の隣＝関東）
    [InlineData(0, 46, RecruitDistance.Far)]              // 北海道 → 沖縄
    public void Distance_UsesRegionBands(int from, int to, RecruitDistance expected)
        => Assert.Equal(expected, PrefectureRegions.DistanceBetween(from, to));

    [Fact]
    public void EveryPrefectureHasRegion()
    {
        for (var id = 0; id < PrefectureNames.Japanese.Count; id++)
            Assert.Equal(PrefectureRegions.RegionOf(id), PrefectureRegions.RegionOf(id));
        Assert.Equal(JapanRegion.HokkaidoTohoku, PrefectureRegions.RegionOf(0));
        Assert.Equal(JapanRegion.Kanto, PrefectureRegions.RegionOf(13));
        Assert.Equal(JapanRegion.Kyushu, PrefectureRegions.RegionOf(46));
    }

    // ── 候補リストの生成 ────────────────────────────────────────

    [Fact]
    public void BuildCandidates_IsDeterministic()
    {
        var a = Build(seed: 800001, level: 2, field: ScoutField.Omakase);
        var b = Build(seed: 800001, level: 2, field: ScoutField.Omakase);
        Assert.Equal(a.Candidates.Select(Key), b.Candidates.Select(Key));
    }

    [Fact]
    public void BuildCandidates_NoSlots_WhenUncontracted()
    {
        var state = Build(seed: 800002, level: 0, field: ScoutField.Omakase);
        Assert.Equal(0, state.Slots);
        Assert.Empty(state.Candidates);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    public void BuildCandidates_SizeIsSlotsTimesCandidatesPerSlot(int level, int slots)
    {
        var state = Build(seed: 800003, level: level, field: ScoutField.Omakase);
        Assert.Equal(slots, state.Slots);
        Assert.Equal(slots * C.CandidatesPerSlot, state.Candidates.Count);
    }

    /// <summary>投球を選ぶと、候補群の投手力（平均）がおまかせより高くなる（ラベルは一切付けない）。</summary>
    [Fact]
    public void BuildCandidates_FieldBiasSkewsTowardChosenAxis()
    {
        double AvgPitching(ScoutField field)
        {
            var total = 0.0;
            var n = 0;
            for (var s = 0; s < 20; s++)
            {
                var state = Build(900000 + s, level: 3, field: field);
                foreach (var c in state.Candidates)
                {
                    total += PlayerStrengthProfile.Compute(c.Player, Strength).Pitching;
                    n++;
                }
            }
            return total / n;
        }

        var pitching = AvgPitching(ScoutField.Pitching);
        var omakase = AvgPitching(ScoutField.Omakase);
        Assert.True(pitching > omakase, $"投球傾き {pitching:F1} がおまかせ {omakase:F1} を上回っていない");
    }

    /// <summary>候補に守備位置・投手適性のラベルは一切出ない（CLAUDE.md「守備位置はゲームが提示しない」）。
    /// 候補が持つのは <see cref="DevelopingPlayer"/> そのもの（受動枠と同じ型）だけで、スカウト専用の
    /// ポジション/役割フィールドは <see cref="ScoutCandidate"/> に存在しない（コンパイル時に保証）。</summary>
    [Fact]
    public void BuildCandidates_CarriesNoPositionLabel()
    {
        var state = Build(800004, level: 3, field: ScoutField.Pitching);
        Assert.NotEmpty(state.Candidates);
        // ScoutCandidate / ScoutReport のプロパティ名を見ても「投手/野手」ラベルが無いことを確認する。
        var candidateProps = typeof(ScoutCandidate).GetProperties().Select(p => p.Name);
        var reportProps = typeof(ScoutReport).GetProperties().Select(p => p.Name);
        foreach (var name in candidateProps.Concat(reportProps))
        {
            Assert.DoesNotContain("Pitcher", name);
            Assert.DoesNotContain("Position", name);
        }
    }

    // ── 見立ての段ゲート ────────────────────────────────────────

    [Fact]
    public void Report_Tier1_OnlyBlurb()
    {
        var state = Build(800005, level: 1, field: ScoutField.Omakase);
        foreach (var c in state.Candidates)
        {
            Assert.Equal(1, c.Report.Tier);
            Assert.NotEmpty(c.Report.BlurbAxis);
            Assert.Equal(0.0, c.Report.Batting);
            Assert.Equal(0.0, c.Report.Contact);
        }
    }

    [Fact]
    public void Report_Tier2_HasCategoryRanksButNoIndividualAbility()
    {
        var state = Build(800006, level: 2, field: ScoutField.Omakase);
        foreach (var c in state.Candidates)
        {
            Assert.Equal(2, c.Report.Tier);
            Assert.True(c.Report.Batting > 0 || c.Report.Mobility > 0 || c.Report.Defense > 0 || c.Report.Pitching > 0);
            Assert.Equal(0.0, c.Report.Contact);
            Assert.Equal(0.0, c.Report.Velocity);
        }
    }

    [Fact]
    public void Report_Tier3_HasIndividualAbilities()
    {
        var state = Build(800007, level: 3, field: ScoutField.Omakase);
        foreach (var c in state.Candidates)
        {
            Assert.Equal(3, c.Report.Tier);
            Assert.True(c.Report.Contact > 0);
            Assert.True(c.Report.Velocity > 0);
        }
    }

    /// <summary>段が低いほど寸評・ランクの見立てノイズが大きい（当たりにくい）。</summary>
    [Fact]
    public void ReportNoise_ShrinksAsTierRises()
        => Assert.True(C.ReportNoiseSdTier1 > C.ReportNoiseSdTier2 && C.ReportNoiseSdTier2 > C.ReportNoiseSdTier3);

    // ── 入部成否の単調性 ───────────────────────────────────────

    [Fact]
    public void SuccessProbability_Prestige_RisesWithFame()
    {
        var p = Candidate(RecruitDistance.SameRegion, Tier.C, CareerPathPreference.Prestige);
        var low = ScoutModel.SuccessProbability(p, 0, Tier.C, hasDorm: true, opportunity: 0.5, C);
        var high = ScoutModel.SuccessProbability(p, 90, Tier.C, hasDorm: true, opportunity: 0.5, C);
        Assert.True(low < high, $"{low:F3} / {high:F3}");
    }

    [Fact]
    public void SuccessProbability_PlayingTime_RisesWithOpportunity()
    {
        var p = Candidate(RecruitDistance.SameRegion, Tier.C, CareerPathPreference.PlayingTime);
        var low = ScoutModel.SuccessProbability(p, 50, Tier.C, hasDorm: true, opportunity: 0.0, C);
        var high = ScoutModel.SuccessProbability(p, 50, Tier.C, hasDorm: true, opportunity: 1.0, C);
        Assert.True(low < high, $"{low:F3} / {high:F3}");
    }

    [Fact]
    public void SuccessProbability_Local_FallsWithDistanceMoreThanOthers()
    {
        double Delta(CareerPathPreference pref) =>
            ScoutModel.SuccessProbability(Candidate(RecruitDistance.SamePrefecture, Tier.C, pref), 50, Tier.C, false, 0.5, C)
            - ScoutModel.SuccessProbability(Candidate(RecruitDistance.Far, Tier.C, pref), 50, Tier.C, false, 0.5, C);

        var localDelta = Delta(CareerPathPreference.Local);
        var prestigeDelta = Delta(CareerPathPreference.Prestige);
        Assert.True(localDelta > prestigeDelta, $"地元志向の距離影響 {localDelta:F3} が他 {prestigeDelta:F3} より大きくない");
    }

    [Fact]
    public void Dorm_ReducesDistancePenaltyOnly()
    {
        var far = Candidate(RecruitDistance.Far, Tier.C, CareerPathPreference.Local);
        Assert.True(
            ScoutModel.SuccessProbability(far, 50, Tier.C, true, 0.5, C)
            > ScoutModel.SuccessProbability(far, 50, Tier.C, false, 0.5, C));

        var home = Candidate(RecruitDistance.SamePrefecture, Tier.C, CareerPathPreference.Local);
        Assert.Equal(
            ScoutModel.SuccessProbability(home, 50, Tier.C, false, 0.5, C),
            ScoutModel.SuccessProbability(home, 50, Tier.C, true, 0.5, C), 9);
    }

    [Fact]
    public void Probability_FallsWithStrongerRivalOnly()
    {
        var baseline = ScoutModel.SuccessProbability(
            Candidate(RecruitDistance.SamePrefecture, Tier.C, CareerPathPreference.Prestige), 50, Tier.C, false, 0.5, C);

        Assert.True(ScoutModel.SuccessProbability(
            Candidate(RecruitDistance.SamePrefecture, Tier.A, CareerPathPreference.Prestige), 50, Tier.C, false, 0.5, C) < baseline);
        Assert.Equal(baseline, ScoutModel.SuccessProbability(
            Candidate(RecruitDistance.SamePrefecture, Tier.F, CareerPathPreference.Prestige), 50, Tier.C, false, 0.5, C), 9);
    }

    [Fact]
    public void Probability_RisesWithVisits_ThenSaturates()
    {
        var prev = -1.0;
        var saturated = false;
        for (var visits = 0; visits <= 12; visits++)
        {
            var p = Candidate(RecruitDistance.SamePrefecture, Tier.C, CareerPathPreference.Prestige);
            p.Visits = visits;
            var prob = ScoutModel.SuccessProbability(p, 50, Tier.C, false, 0.5, C);
            Assert.True(prob >= prev, "訪問を増やして下がった");
            if (visits > 0 && System.Math.Abs(prob - prev) < 1e-12) saturated = true;
            prev = prob;
            Assert.InRange(prob, C.ProbabilityMin, C.ProbabilityMax);
        }
        Assert.True(saturated, "訪問の上積みが頭打ちになっていない");
    }

    [Fact]
    public void Visit_IncrementsAndCanRaiseRival()
    {
        var list = Build(800008, level: 3, field: ScoutField.Omakase);
        var p = list.Candidates[0];
        var startRank = ScoutModel.TierRank(p.RivalTier);
        var rng = new Xoshiro256Random(999);

        var raised = 0;
        for (var i = 0; i < 50; i++) if (ScoutModel.Visit(p, C, rng)) raised++;

        Assert.Equal(50, p.Visits);
        Assert.True(raised > 0, "50回訪問して争奪戦が一度も起きない");
        Assert.True(ScoutModel.TierRank(p.RivalTier) >= startRank);
        Assert.True(ScoutModel.TierRank(p.RivalTier) <= ScoutModel.TierRank(Tier.S));
    }

    [Fact]
    public void Resolve_OnlyTargeted_AndIsIdempotent()
    {
        var state = Build(800009, level: 3, field: ScoutField.Omakase);
        state.Candidates[0].Targeted = true;
        state.Candidates[1].Targeted = true;
        var roster = new List<DevelopingPlayer> { new() };

        var won = ScoutModel.Resolve(state, 60, Tier.C, true, roster, Strength, C, new Xoshiro256Random(555));

        Assert.True(state.Resolved);
        foreach (var c in state.Candidates) Assert.Equal(c.Targeted, c.Decided);
        Assert.All(won, c => Assert.True(c.Targeted && c.Succeeded));

        var again = ScoutModel.Resolve(state, 60, Tier.C, true, roster, Strength, C, new Xoshiro256Random(1));
        Assert.Empty(again);
    }

    [Fact]
    public void OpportunityScore_HigherWhenRosterIsWeaker()
    {
        var candidate = Build(800010, level: 3, field: ScoutField.Batting).Candidates[0].Player;

        var weakRoster = new List<DevelopingPlayer> { WeakPlayer(), WeakPlayer() };
        var strongRoster = new List<DevelopingPlayer> { StrongPlayer(), StrongPlayer() };

        var weak = ScoutModel.OpportunityScore(candidate, weakRoster, Strength);
        var strong = ScoutModel.OpportunityScore(candidate, strongRoster, Strength);
        Assert.True(weak >= strong, $"弱いロスターの方が空きが大きくない: weak={weak:F2} strong={strong:F2}");
    }

    /// <summary>
    /// スカウトは受動枠の生成列を1ビットも乱さない（不変条件#2）。呼び出し側が別ストリームを渡す前提を、
    /// 「同じ母種から Fork した2本」で確かめる＝スカウトを何回引いても新入生は変わらない。
    /// </summary>
    [Fact]
    public void Scouting_DoesNotDisturbPassiveIntake()
    {
        const ulong master = 20261109UL;

        IReadOnlyList<DevelopingPlayer> Intake() =>
            ProspectGenerator.Intake(2, Roster, new Xoshiro256Random(master ^ 0x5535_0002UL));

        var before = Intake().Select(p => p.Name).ToList();

        for (var i = 0; i < 5; i++)
        {
            var state = ScoutModel.BuildCandidates(
                2, ScoutField.Batting, level: 3, slots: 4, homePrefectureId: 13, homeTier: Tier.C,
                talentCenter: Roster.TalentCenterDefault, contractQualityAdd: 6, roster: Roster, c: C,
                strengthCoeff: Strength, rng: new Xoshiro256Random(master ^ (0x5588_0000UL ^ (ulong)i)));
            foreach (var c in state.Candidates) ScoutModel.Visit(c, C, new Xoshiro256Random((ulong)i));
        }

        Assert.Equal(before, Intake().Select(p => p.Name).ToList());
    }

    // ── 補助 ────────────────────────────────────────────────

    private static ScoutingState Build(int seed, int level, ScoutField field) => ScoutModel.BuildCandidates(
        yearIndex: 2, field: field, level: level, slots: ScoutModel.SlotsForLevel(level, Contracts),
        homePrefectureId: 13, homeTier: Tier.C, talentCenter: Roster.TalentCenterDefault,
        contractQualityAdd: level switch { 2 => 3, 3 => 6, _ => 0 }, roster: Roster, c: C,
        strengthCoeff: Strength, rng: new Xoshiro256Random((ulong)seed));

    private static ScoutCandidate Candidate(RecruitDistance d, Tier rival, CareerPathPreference pref)
        => new() { Distance = d, RivalTier = rival, Preference = pref };

    private static string Key(ScoutCandidate p)
        => p.Player.Name + ":" + p.PrefectureId + ":" + p.Distance + ":" + p.RivalTier + ":" + p.Preference;

    private static DevelopingPlayer WeakPlayer()
    {
        var p = new DevelopingPlayer { Name = "弱" };
        foreach (var k in AbilityKinds.All) p.SetLevel(k, 20);
        return p;
    }

    private static DevelopingPlayer StrongPlayer()
    {
        var p = new DevelopingPlayer { Name = "強" };
        foreach (var k in AbilityKinds.All) p.SetLevel(k, 90);
        return p;
    }
}

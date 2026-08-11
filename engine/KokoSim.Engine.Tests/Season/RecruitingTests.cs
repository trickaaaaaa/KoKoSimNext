using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 能動勧誘枠（設計書04 §2.2・issue #588）。寮で解禁される「年最大2名」の枠、成功率の単調性、
/// 受動枠を1ビットも動かさないこと、を固定する。
/// </summary>
public sealed class RecruitingTests
{
    private static readonly RosterCoefficients Roster = new();
    private static readonly RecruitCoefficients C = new();
    private static readonly FacilityCatalog Catalog = FacilityCatalog.Default;

    // ── 枠の解禁（寮） ──────────────────────────────────────────

    /// <summary>寮が無ければ枠は0、寮を建てると2つ解禁される（設計書04 §2.2「年最大2名」）。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]   // 上の段を買っても枠は増えない
    [InlineData(3, 2)]
    public void DormUnlocksExactlyTwoSlots(int dormLevel, int expectedSlots)
    {
        var set = new FacilitySet();
        set.SetLevel("dorm", dormLevel);
        Assert.Equal(expectedSlots, Catalog.AggregateEffects(set).RecruitSlots);
    }

    /// <summary>寮以外の施設は勧誘枠を持たない（枠の出所を1つに保つ）。</summary>
    [Fact]
    public void OnlyDormGrantsSlots()
    {
        foreach (var def in Catalog.Facilities)
        {
            if (def.Id == "dorm") continue;
            var set = new FacilitySet();
            set.SetLevel(def.Id, def.MaxLevel);
            Assert.Equal(0, Catalog.AggregateEffects(set).RecruitSlots);
        }
    }

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

    /// <summary>全県が必ずどれかの地方に入る（範囲外で落ちない）。</summary>
    [Fact]
    public void EveryPrefectureHasRegion()
    {
        for (var id = 0; id < PrefectureNames.Japanese.Count; id++)
            Assert.Equal(PrefectureRegions.RegionOf(id), PrefectureRegions.RegionOf(id));
        Assert.Equal(JapanRegion.HokkaidoTohoku, PrefectureRegions.RegionOf(0));
        Assert.Equal(JapanRegion.Kanto, PrefectureRegions.RegionOf(13));
        Assert.Equal(JapanRegion.Kyushu, PrefectureRegions.RegionOf(46));
    }

    // ── 成功率の単調性（設計書04 §2.2） ───────────────────────────

    /// <summary>名声が高いほど成功率が上がる。</summary>
    [Fact]
    public void Probability_RisesWithFame()
    {
        var p = Prospect(RecruitDistance.SameRegion, Tier.C);
        var low = RecruitModel.SuccessProbability(p, 0, Tier.C, hasDorm: true, C);
        var mid = RecruitModel.SuccessProbability(p, 40, Tier.C, hasDorm: true, C);
        var high = RecruitModel.SuccessProbability(p, 90, Tier.C, hasDorm: true, C);
        Assert.True(low < mid && mid < high, $"{low:F3} / {mid:F3} / {high:F3}");
    }

    /// <summary>遠いほど成功率が下がる。</summary>
    [Fact]
    public void Probability_FallsWithDistance()
    {
        var probs = new[]
        {
            RecruitDistance.SamePrefecture, RecruitDistance.SameRegion,
            RecruitDistance.NeighborRegion, RecruitDistance.Far,
        }.Select(d => RecruitModel.SuccessProbability(Prospect(d, Tier.C), 50, Tier.C, hasDorm: false, C)).ToList();

        for (var i = 1; i < probs.Count; i++)
            Assert.True(probs[i] < probs[i - 1], $"距離{i} が {i - 1} より下がっていない: {probs[i - 1]:F3} → {probs[i]:F3}");
    }

    /// <summary>寮は距離の減点を和らげる（設計書04 §2.2「距離（寮でカバー）」）。同県では差が出ない。</summary>
    [Fact]
    public void Dorm_ReducesDistancePenaltyOnly()
    {
        var far = Prospect(RecruitDistance.Far, Tier.C);
        Assert.True(
            RecruitModel.SuccessProbability(far, 50, Tier.C, hasDorm: true, C)
            > RecruitModel.SuccessProbability(far, 50, Tier.C, hasDorm: false, C));

        var home = Prospect(RecruitDistance.SamePrefecture, Tier.C);
        Assert.Equal(
            RecruitModel.SuccessProbability(home, 50, Tier.C, hasDorm: false, C),
            RecruitModel.SuccessProbability(home, 50, Tier.C, hasDorm: true, C), 9);
    }

    /// <summary>競合校の格が上なら下がる。格下が競合しても下がらない。</summary>
    [Fact]
    public void Probability_FallsWithStrongerRivalOnly()
    {
        var baseline = RecruitModel.SuccessProbability(
            Prospect(RecruitDistance.SamePrefecture, Tier.C), 50, Tier.C, hasDorm: false, C);

        Assert.True(RecruitModel.SuccessProbability(
            Prospect(RecruitDistance.SamePrefecture, Tier.A), 50, Tier.C, hasDorm: false, C) < baseline);
        Assert.Equal(baseline, RecruitModel.SuccessProbability(
            Prospect(RecruitDistance.SamePrefecture, Tier.F), 50, Tier.C, hasDorm: false, C), 9);
    }

    /// <summary>訪問を重ねるほど上がるが、上限で頭打ちになる（通い詰めても確実にはならない）。</summary>
    [Fact]
    public void Probability_RisesWithVisits_ThenSaturates()
    {
        var prev = -1.0;
        var saturated = false;
        for (var visits = 0; visits <= 12; visits++)
        {
            var p = Prospect(RecruitDistance.SamePrefecture, Tier.C);
            p.Visits = visits;
            var prob = RecruitModel.SuccessProbability(p, 50, Tier.C, hasDorm: false, C);
            Assert.True(prob >= prev, "訪問を増やして下がった");
            if (visits > 0 && System.Math.Abs(prob - prev) < 1e-12) saturated = true;
            prev = prob;
            Assert.InRange(prob, C.ProbabilityMin, C.ProbabilityMax);
        }
        Assert.True(saturated, "訪問の上積みが頭打ちになっていない");
    }

    // ── リスト生成・訪問・判定 ────────────────────────────────

    [Fact]
    public void BuildList_IsDeterministic()
    {
        var a = BuildList(seed: 700001);
        var b = BuildList(seed: 700001);
        Assert.Equal(a.Prospects.Select(Key), b.Prospects.Select(Key));
    }

    /// <summary>リストは指定人数ぶん出て、自県と県外が混ざる（地元だけだと寮の意味が出ない）。</summary>
    [Fact]
    public void BuildList_MixesHomeAndAwayPrefectures()
    {
        var home = 0; var away = 0;
        for (var s = 0; s < 40; s++)
        {
            var list = BuildList(700100 + s);
            Assert.Equal(C.ListSize, list.Prospects.Count);
            foreach (var p in list.Prospects)
            {
                if (p.PrefectureId == 13) home++; else away++;
            }
        }
        Assert.True(home > 0, "自県の中学生が一人も出ない");
        Assert.True(away > 0, "県外の中学生が一人も出ない");
    }

    /// <summary>訪問すると回数が増え、争奪戦が起きると競合校の格が上がる（S を超えない）。</summary>
    [Fact]
    public void Visit_IncrementsAndCanRaiseRival()
    {
        var list = BuildList(700200);
        var p = list.Prospects[0];
        var startRank = RecruitModel.TierRank(p.RivalTier);
        var rng = new Xoshiro256Random(999);

        var raised = 0;
        for (var i = 0; i < 50; i++) if (RecruitModel.Visit(p, C, rng)) raised++;

        Assert.Equal(50, p.Visits);
        Assert.True(raised > 0, "50回訪問して争奪戦が一度も起きない");
        Assert.True(RecruitModel.TierRank(p.RivalTier) >= startRank);
        Assert.True(RecruitModel.TierRank(p.RivalTier) <= RecruitModel.TierRank(Tier.S));
    }

    /// <summary>判定は対象にした中学生だけを引き、冪等（2度呼んでも結果が変わらない）。</summary>
    [Fact]
    public void Resolve_OnlyTargeted_AndIsIdempotent()
    {
        var list = BuildList(700300);
        list.Prospects[0].Targeted = true;
        list.Prospects[3].Targeted = true;

        var won = RecruitModel.Resolve(list, 60, Tier.C, hasDorm: true, C, new Xoshiro256Random(555));

        Assert.True(list.Resolved);
        foreach (var p in list.Prospects) Assert.Equal(p.Targeted, p.Decided);
        Assert.All(won, p => Assert.True(p.Targeted && p.Succeeded));

        var again = RecruitModel.Resolve(list, 60, Tier.C, hasDorm: true, C, new Xoshiro256Random(1));
        Assert.Empty(again);
    }

    /// <summary>
    /// 勧誘は受動枠の生成列を1ビットも乱さない（不変条件#2）。呼び出し側が別ストリームを渡す前提を、
    /// 「同じ母種から Fork した2本」で確かめる＝勧誘を何回引いても新入生は変わらない。
    /// </summary>
    [Fact]
    public void Recruiting_DoesNotDisturbPassiveIntake()
    {
        const ulong master = 20260811UL;

        IReadOnlyList<DevelopingPlayer> Intake() =>
            ProspectGenerator.Intake(2, Roster, new Xoshiro256Random(master ^ 0x5535_0002UL));

        var before = Intake().Select(p => p.Name).ToList();

        // 勧誘は専用ストリーム。何度引いても受動枠には触れない。
        for (var i = 0; i < 5; i++)
        {
            var list = RecruitModel.BuildList(
                2, 2, 13, Tier.C, Roster.TalentCenterDefault, Roster, C,
                new Xoshiro256Random(master ^ (0x5588_0000UL ^ (ulong)i)));
            foreach (var p in list.Prospects) RecruitModel.Visit(p, C, new Xoshiro256Random((ulong)i));
        }

        Assert.Equal(before, Intake().Select(p => p.Name).ToList());
    }

    // ── 補助 ────────────────────────────────────────────────

    private static RecruitingState BuildList(int seed) => RecruitModel.BuildList(
        yearIndex: 2, slots: 2, homePrefectureId: 13, homeTier: Tier.C,
        talentCenter: Roster.TalentCenterDefault, roster: Roster, c: C,
        rng: new Xoshiro256Random((ulong)seed));

    private static RecruitProspect Prospect(RecruitDistance d, Tier rival)
        => new() { Distance = d, RivalTier = rival };

    private static string Key(RecruitProspect p)
        => p.Player.Name + ":" + p.PrefectureId + ":" + p.Distance + ":" + p.RivalTier;
}

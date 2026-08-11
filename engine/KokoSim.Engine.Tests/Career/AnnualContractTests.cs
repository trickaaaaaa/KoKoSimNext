using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>
/// 年契約（設計書04 §1.4a）。施設と違い「いまの段だけが効く（累積しない）」こと、
/// 未契約なら従来と1ビット一致すること、部費から毎年引かれることを固定する。
/// </summary>
public sealed class AnnualContractTests
{
    private static readonly ContractCatalog Catalog = ContractCatalog.Default;

    // ── カタログの形 ───────────────────────────────────────────────

    [Fact]
    public void EmptySet_HasNoCostAndNoEffect()
    {
        var e = Catalog.Aggregate(new ContractSet());
        Assert.Equal(ContractEffects.None, e);
    }

    /// <summary>
    /// 新規ゲームの既定は<b>未契約</b>（監督決定 2026-08-11）。監督が一度も選んでいない年額を
    /// 既定で引き続けない＝スカウトは「払っている間だけ効く」ものにする。
    /// </summary>
    [Fact]
    public void Initial_IsEmpty_NoCostNoEffect()
    {
        var e = Catalog.Aggregate(ContractSet.Initial());

        Assert.Equal(ContractEffects.None, e);
        Assert.Equal(0, e.AnnualCost, 9);
        Assert.Equal(0, e.ScoutTalentAdd, 9);
        Assert.Equal(0, e.ProspectDisclosure, 9);
    }

    /// <summary>
    /// 年契約が無かった版（セーブ v1/v2）の読み替えは、当時と同額・同効果のスカウト網 Lv1。
    /// 新規ゲームの既定（未契約）とは別物＝旧セーブの収支を後から変えない。
    /// </summary>
    [Fact]
    public void LegacyScoutingLevel1_CostsExactlyTheLegacyScoutFee()
    {
        var e = Catalog.Aggregate(ContractSet.LegacyScoutingLevel1());

        Assert.Equal(new CareerCoefficients().ScoutCost, e.AnnualCost, 9);
        Assert.Equal(0, e.ScoutTalentAdd, 9);
        Assert.False(e.OutOfPrefectureOpponents);
    }

    /// <summary>
    /// 年契約の核心: 段を上げても<b>効果は累積しない</b>（Lv2 が Lv1 を置き換える）。
    /// 施設（FacilityCatalog.Aggregate が 1..lv を合算する）との決定的な違い。
    /// </summary>
    [Theory]
    [InlineData(1, 30, 0)]
    [InlineData(2, 80, 3)]
    [InlineData(3, 140, 6)]
    public void Scouting_ReplacesLowerTier_DoesNotAccumulate(int level, double cost, double talentAdd)
    {
        var set = new ContractSet();
        set.SetLevel("scouting", level);
        var e = Catalog.Aggregate(set);

        Assert.Equal(cost, e.AnnualCost, 9);
        Assert.Equal(talentAdd, e.ScoutTalentAdd, 9);
    }

    [Fact]
    public void Cancelling_RestoresZero()
    {
        var set = new ContractSet();
        set.SetLevel("scouting", 3);
        set.SetLevel("coach_batting", 1);
        Assert.Equal(190, Catalog.Aggregate(set).AnnualCost, 9);   // 140 + 50

        // 解約＝段を0へ戻す。年額も効果もその場で消える（買い切りの施設にはできない挙動）。
        set.SetLevel("scouting", 0);
        set.SetLevel("coach_batting", 0);
        Assert.Equal(ContractEffects.None, Catalog.Aggregate(set));
    }

    [Fact]
    public void UnknownLevel_IsClamped()
    {
        var set = new ContractSet();
        set.SetLevel("scouting", 99);          // 上限超え → MaxLevel(3) 扱い
        set.SetLevel("no_such_contract", 2);   // カタログに無いIDは無視
        var e = Catalog.Aggregate(set);

        Assert.Equal(140, e.AnnualCost, 9);
        Assert.Equal(6, e.ScoutTalentAdd, 9);
    }

    [Fact]
    public void Expedition_UnlocksOutOfPrefectureOpponents()
    {
        var set = new ContractSet();
        Assert.False(Catalog.Aggregate(set).OutOfPrefectureOpponents);

        set.SetLevel("expedition", 1);
        var e = Catalog.Aggregate(set);
        Assert.True(e.OutOfPrefectureOpponents);
        Assert.Equal(20, e.AnnualCost, 9);
    }

    [Fact]
    public void Coaches_AddToTheirOwnFieldOnly()
    {
        var set = new ContractSet();
        set.SetLevel("coach_batting", 1);
        var e = Catalog.Aggregate(set);

        Assert.Equal(10, e.CoachingBattingAdd, 9);
        Assert.Equal(0, e.CoachingPitchingAdd, 9);
        Assert.Equal(0, e.CoachingDefenseAdd, 9);
    }

    [Fact]
    public void AllContractsAtMax_CostAsDocumented()
    {
        var set = new ContractSet();
        foreach (var d in Catalog.Contracts) set.SetLevel(d.Id, d.MaxLevel);
        var e = Catalog.Aggregate(set);

        // 140（スカウト網Lv3）+ 20（遠征）+ 50×3（専門コーチ3分野）= 310万/年。
        // 自由に使える上限（信頼度100で115万）の約3倍＝全部は絶対に抱えられない。
        Assert.Equal(310, e.AnnualCost, 9);
        Assert.Equal(6, e.ScoutTalentAdd, 9);
        Assert.True(e.OutOfPrefectureOpponents);
    }

    // ── 指導力への上乗せ ───────────────────────────────────────────

    [Fact]
    public void CoachingProfile_WithoutContracts_MatchesLegacyExactly()
    {
        var m = new Manager { CoachingBatting = 41, CoachingPitching = 52, CoachingDefense = 63 };

        var legacy = CoachingProfile.FromManager(m);
        var withNone = CoachingProfile.FromManager(m, ContractEffects.None);

        Assert.Equal(legacy, withNone);
        Assert.Equal(41, legacy.Batting, 9);
    }

    [Fact]
    public void CoachingProfile_CoachRaisesEffectiveLevel_ButNotTheManager()
    {
        var m = new Manager { CoachingBatting = 41, CoachingPitching = 52, CoachingDefense = 63 };
        var set = new ContractSet();
        set.SetLevel("coach_pitching", 1);

        var p = CoachingProfile.FromManager(m, Catalog.Aggregate(set));

        Assert.Equal(41, p.Batting, 9);
        Assert.Equal(62, p.Pitching, 9);   // 52 + 10
        Assert.Equal(63, p.Defense, 9);
        // 監督自身は書き換えない＝解約すればその年から元に戻る。
        Assert.Equal(52, m.CoachingPitching, 9);
    }

    /// <summary>
    /// 上限99でクランプ<b>しない</b>（意図した仕様）。上限に張り付いた監督でもコーチを雇う意味を残す。
    /// </summary>
    [Fact]
    public void CoachingProfile_IsNotClampedAtTheManagerCap()
    {
        var m = new Manager { CoachingBatting = 99, CoachingPitching = 99, CoachingDefense = 99 };
        var set = new ContractSet();
        set.SetLevel("coach_batting", 1);

        Assert.Equal(109, CoachingProfile.FromManager(m, Catalog.Aggregate(set)).Batting, 9);
    }

    // ── 部費からの年額支払い ────────────────────────────────────────

    private static Manager Fresh() => new() { Funds = 500, Trust = 50, Fame = 10 };

    private static double FundsAfterOneYear(ContractEffects? contracts)
    {
        var m = Fresh();
        ManagerSeasonUpdate.Apply(
            m, reachedKoshien: false, nationalChampion: false, wins: 3,
            new Xoshiro256Random(1234), contracts: contracts);
        return m.Funds;
    }

    /// <summary>年契約を渡さない経路（キャリアシム）は従来どおり＝1ビット一致（不変条件#2）。</summary>
    [Fact]
    public void YearEnd_WithoutContracts_DeductsLegacyScoutCost()
    {
        var c = new CareerCoefficients();
        var m = Fresh();
        var before = m.Funds;

        ManagerSeasonUpdate.Apply(m, false, false, 3, new Xoshiro256Random(1234));

        var expected = before
                       + c.AnnualBudgetBase + c.BudgetPerTrust * m.Trust
                       - (c.SummerCampCost + c.WinterCampCost + c.ScoutCost);
        Assert.Equal(expected, m.Funds, 6);
    }

    /// <summary>旧セーブの読み替え（スカウト網 Lv1）なら年契約経路でも従来と同額しか引かれない。</summary>
    [Fact]
    public void YearEnd_WithLegacyScoutingContract_MatchesLegacyFunds()
    {
        Assert.Equal(
            FundsAfterOneYear(null),
            FundsAfterOneYear(Catalog.Aggregate(ContractSet.LegacyScoutingLevel1())),
            6);
    }

    /// <summary>新規ゲームの既定（未契約）は、従来のスカウト費15万ぶんだけ支出が軽い。</summary>
    [Fact]
    public void YearEnd_WithInitialContracts_SavesTheLegacyScoutFee()
    {
        var c = new CareerCoefficients();
        Assert.Equal(
            FundsAfterOneYear(null) + c.ScoutCost,
            FundsAfterOneYear(Catalog.Aggregate(ContractSet.Initial())),
            6);
    }

    [Fact]
    public void YearEnd_ExtraContracts_CostMore()
    {
        var set = new ContractSet();
        set.SetLevel("scouting", 3);        // 140万
        set.SetLevel("coach_batting", 1);   // 50万
        var rich = FundsAfterOneYear(Catalog.Aggregate(set));

        // 従来経路（スカウト費＝スカウト網Lv1と同額）より 160万ぶん多く出ていく。
        Assert.Equal(FundsAfterOneYear(null) - 160, rich, 6);
    }

    [Fact]
    public void YearEnd_FundsNeverGoNegative()
    {
        var set = new ContractSet();
        foreach (var d in Catalog.Contracts) set.SetLevel(d.Id, d.MaxLevel);

        var m = new Manager { Funds = 0, Trust = 0 };
        ManagerSeasonUpdate.Apply(m, false, false, 0, new Xoshiro256Random(7), contracts: Catalog.Aggregate(set));

        Assert.Equal(0, m.Funds, 9);
    }
}

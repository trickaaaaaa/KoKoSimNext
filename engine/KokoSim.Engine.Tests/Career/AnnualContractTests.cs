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
    [InlineData(1, 100, 0)]
    [InlineData(2, 250, 3)]
    [InlineData(3, 420, 6)]
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
        Assert.Equal(580, Catalog.Aggregate(set).AnnualCost, 9);   // 420 + 160

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

        Assert.Equal(420, e.AnnualCost, 9);
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
        Assert.Equal(60, e.AnnualCost, 9);
    }

    [Fact]
    public void Coaches_AddToTheirOwnFieldOnly()
    {
        var set = new ContractSet();
        set.SetLevel("coach_batting", 1);
        var e = Catalog.Aggregate(set);

        Assert.Equal(50, e.CoachingBattingAdd, 9);
        Assert.Equal(0, e.CoachingPitchingAdd, 9);
        Assert.Equal(0, e.CoachingDefenseAdd, 9);
    }

    [Fact]
    public void AllContractsAtMax_CostAsDocumented()
    {
        var set = new ContractSet();
        foreach (var d in Catalog.Contracts) set.SetLevel(d.Id, d.MaxLevel);
        var e = Catalog.Aggregate(set);

        // 420（スカウト網Lv3）+ 60（遠征）+ 160×3（専門コーチ3分野）= 960万/年（issue #1299 案B）。
        // 強豪の定常の「自由に使える額」（約940万）でも届かない＝甲子園に出た年だけ全部取れる。
        Assert.Equal(960, e.AnnualCost, 9);
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
        Assert.Equal(102, p.Pitching, 9);   // 52 + 50
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

        Assert.Equal(149, CoachingProfile.FromManager(m, Catalog.Aggregate(set)).Batting, 9);
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
                       + c.SchoolInvestment + c.BoosterDonationPerFame * m.Fame   // 寄付も部費へ（issue #1082）
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
        set.SetLevel("scouting", 3);        // 420万
        set.SetLevel("coach_batting", 1);   // 160万
        var rich = FundsAfterOneYear(Catalog.Aggregate(set));

        // 従来経路（スカウト費＝スカウト網Lv1と同額の100万）より 480万ぶん多く出ていく。
        Assert.Equal(FundsAfterOneYear(null) - 480, rich, 6);
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

    // ── 解約は年度末に効く（issue #1298） ──────────────────────────────

    /// <summary>第0週に契約→第45週に解約しても、第46週の総括で年額は引かれ、効果は年度末まで残る。</summary>
    [Fact]
    public void Cancel_MidYear_StillChargesAtYearEnd_AndKeepsEffectUntilThen()
    {
        var set = new ContractSet();
        set.SetLevel("coach_batting", 1);   // 第0週に契約
        set.ScheduleCancel("coach_batting"); // 第45週に解約の操作

        Assert.Equal(1, set.LevelOf("coach_batting"));
        Assert.Equal(50, Catalog.Aggregate(set).CoachingBattingAdd, 9);   // 効果は年度末まで残る

        // 第46週の総括: 契約したままの年と同じ額が引かれる。
        var kept = new ContractSet();
        kept.SetLevel("coach_batting", 1);
        Assert.Equal(FundsAfterOneYear(Catalog.Aggregate(kept)), FundsAfterOneYear(Catalog.Aggregate(set)), 9);
        Assert.Equal(FundsAfterOneYear(null) - 160 + new CareerCoefficients().ScoutCost, FundsAfterOneYear(Catalog.Aggregate(set)), 6);

        // 総括の後にはじめて段が下がり、効果も年額も消える（次の年から）。
        set.ApplyScheduled();
        Assert.Equal(0, set.LevelOf("coach_batting"));
        Assert.Empty(set.Scheduled);
        Assert.Equal(ContractEffects.None, Catalog.Aggregate(set));
    }

    [Fact]
    public void Schedule_CanBeClearedBeforeYearEnd()
    {
        var set = new ContractSet();
        set.SetLevel("expedition", 1);
        set.ScheduleCancel("expedition");
        Assert.Equal(0, set.ScheduledLevelOf("expedition"));

        set.ClearSchedule("expedition");
        set.ApplyScheduled();
        Assert.Equal(1, set.LevelOf("expedition"));
    }

    [Fact]
    public void Schedule_ToLevelAtOrAboveCurrent_IsNotAReservation()
    {
        var set = new ContractSet();
        set.SetLevel("scouting", 2);
        set.ScheduleLevel("scouting", 1);
        Assert.Equal(1, set.ScheduledLevelOf("scouting"));
        set.ScheduleLevel("scouting", 2);   // いまの段へ戻す＝取り消し
        Assert.Null(set.ScheduledLevelOf("scouting"));
    }

    /// <summary>決め直して段を直接据えたら、古い予約は捨てる（契約し直した＝継続の意思）。</summary>
    [Fact]
    public void SetLevel_DropsThePendingReservation()
    {
        var set = new ContractSet();
        set.SetLevel("scouting", 1);
        set.ScheduleCancel("scouting");
        set.SetLevel("scouting", 2);
        set.ApplyScheduled();
        Assert.Equal(2, set.LevelOf("scouting"));
    }

    /// <summary>下げる予約は2段階の下げ（Lv3→Lv1）にも使える。</summary>
    [Fact]
    public void Schedule_ToLowerNonZeroLevel_AppliesAtYearEnd()
    {
        var set = new ContractSet();
        set.SetLevel("scouting", 3);
        set.ScheduleLevel("scouting", 1);
        Assert.Equal(3, set.LevelOf("scouting"));
        set.ApplyScheduled();
        Assert.Equal(1, set.LevelOf("scouting"));
    }

    // ── 収支の釣り合い（issue #1299 案B・2026-11-23 オーナー決定） ─────────────

    /// <summary>
    /// 寄付イベント（OB会・後援会・地元企業・道具破損）の部費の期待値[万円/年]。data/events.yaml の発火を
    /// 50週×400年回した実測（out/economy-proposal-1299.md §1.3）。年度末の総括の外で入る収入。
    /// </summary>
    private const double EventFundsPerYear = 140;

    /// <summary>年契約に回せる額[万円/年]＝総括の収入＋寄付イベントの期待値－合宿（甲子園の一時金は入れない）。</summary>
    private static double FreeFunds(double trust, double fame, double prize)
    {
        var c = new CareerCoefficients();
        return CareerEngine.AnnualFundsNet(trust, fame, contractCost: 0, c) + prize + EventFundsPerYear;
    }

    private static double CostOf(params (string Id, int Level)[] levels)
    {
        var set = new ContractSet();
        foreach (var (id, lv) in levels) set.SetLevel(id, lv);
        return Catalog.Aggregate(set).AnnualCost;
    }

    /// <summary>新任（信頼30・名声0）でもスカウト網Lv1かコーチ1人は払えるが、その2つは同時に持てない。</summary>
    [Fact]
    public void Balance_Newcomer_CanAffordOneButNotBoth()
    {
        var free = FreeFunds(trust: 30, fame: 0, prize: 0);
        Assert.True(CostOf(("scouting", 1)) <= free, $"Lv1 {CostOf(("scouting", 1))} > {free}");
        Assert.True(CostOf(("coach_batting", 1)) <= free);
        Assert.True(CostOf(("scouting", 1), ("coach_batting", 1)) > free);
    }

    /// <summary>強豪の定常（信頼100・名声47・年9勝の大会報奨）でも全部の最上段は維持できない（甲子園の一時金の年だけ届く）。</summary>
    [Fact]
    public void Balance_PowerhouseSteadyState_CannotKeepEverything()
    {
        var c = new CareerCoefficients();
        var free = FreeFunds(trust: 100, fame: 47, prize: 90);   // 大会報奨（#1469 T1）: 9勝×10万
        var all = new ContractSet();
        foreach (var d in Catalog.Contracts) all.SetLevel(d.Id, d.MaxLevel);
        Assert.True(Catalog.Aggregate(all).AnnualCost > free, $"全部 {Catalog.Aggregate(all).AnnualCost} <= {free}");
        Assert.True(Catalog.Aggregate(all).AnnualCost <= free + c.KoshienYearBonus);
        // Lv3＋コーチ2人なら取れる＝段階が上がるほど取れる組み合わせが増える。
        Assert.True(CostOf(("scouting", 3), ("coach_batting", 1), ("coach_pitching", 1)) <= free);
    }

    /// <summary>中堅の定常（信頼75・名声31・年3勝）は Lv3＋コーチ1人 までで、Lv3＋コーチ2人は取れない。</summary>
    [Fact]
    public void Balance_MidTierSteadyState_ChoosesBetweenCombinations()
    {
        var free = FreeFunds(trust: 75, fame: 31, prize: 30);   // 大会報奨（#1469 T1）: 3勝×10万
        Assert.True(CostOf(("scouting", 3), ("coach_batting", 1)) <= free);
        Assert.True(CostOf(("scouting", 2), ("coach_batting", 1), ("coach_pitching", 1)) <= free);
        Assert.True(CostOf(("scouting", 3), ("coach_batting", 1), ("coach_pitching", 1)) > free);
    }
}

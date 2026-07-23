using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 相手校AIの先発選択（エース温存, issue #42）の決定論選抜ロジックの検証。
/// AceRestSelector は rng を注入しない純関数（校ID・試合日から専用ストリームを起こす）なので、
/// schoolId や matchDay を振って多数回呼ぶことで確率挙動を検証できる。
/// </summary>
public sealed class AceRestSelectorTests
{
    private static School Sch(int id, double strength, SchoolStyle style = SchoolStyle.Standard) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength, Style = style,
    };

    private static AceRestContext Ctx(Tier opponentTier, int roundsRemaining, int matchDay = 10, TournamentPitchLedger? ledger = null)
        => new(opponentTier, roundsRemaining, matchDay, ledger);

    [Fact]
    public void BelowMinTier_NeverRests_EvenWithFavorableCoefficients()
    {
        var ai = new EnemyAiCoefficients { AceRestMinTier = 3, AceRestFloor = 1.0, AceRestCap = 1.0 };
        for (var id = 0; id < 50; id++)
        {
            var school = Sch(id, 45);   // Tier E(2) < AceRestMinTier(3) ＝ ゲート未満
            Assert.False(AceRestSelector.ShouldRestAce(school, Ctx(Tier.G, 3, matchDay: id), ai));
        }
    }

    [Fact]
    public void Final_NeverRests_NoRoundsLeftToSaveArmFor()
    {
        var ai = new EnemyAiCoefficients { AceRestFloor = 1.0, AceRestCap = 1.0 };
        var school = Sch(1, 90);   // S級、ティアゲートは通る
        for (var id = 0; id < 50; id++)
            Assert.False(AceRestSelector.ShouldRestAce(school, Ctx(Tier.G, roundsRemaining: 1, matchDay: id), ai));
    }

    [Fact]
    public void FloorEqualsCapOne_AlwaysRests_WhenGatesPass()
    {
        var ai = new EnemyAiCoefficients { AceRestFloor = 1.0, AceRestCap = 1.0 };
        var school = Sch(1, 90);
        for (var id = 0; id < 50; id++)
            Assert.True(AceRestSelector.ShouldRestAce(school, Ctx(Tier.G, roundsRemaining: 3, matchDay: id), ai));
    }

    [Fact]
    public void FloorEqualsCapZero_NeverRests()
    {
        var ai = new EnemyAiCoefficients { AceRestFloor = 0.0, AceRestCap = 0.0 };
        var school = Sch(1, 90);
        for (var id = 0; id < 50; id++)
            Assert.False(AceRestSelector.ShouldRestAce(school, Ctx(Tier.G, roundsRemaining: 3, matchDay: id), ai));
    }

    [Fact]
    public void AceDependentStyle_RestsFarLessThanDefensiveMinded_OverManySchools()
    {
        var ai = new EnemyAiCoefficients
        {
            AceRestBase = 0.3, AceRestTierGapWeight = 0, AceRestRoundsRemainingWeight = 0, AceRestFatigueWeight = 0,
            AceRestFloor = 0.0, AceRestCap = 0.9,
        };
        var aceDependentRests = 0;
        var defensiveMindedRests = 0;
        const int n = 400;
        for (var day = 10; day < 10 + n; day++)
        {
            if (AceRestSelector.ShouldRestAce(Sch(1, 90, SchoolStyle.AceDependent), Ctx(Tier.G, 3, day), ai)) aceDependentRests++;
            if (AceRestSelector.ShouldRestAce(Sch(1, 90, SchoolStyle.DefensiveMinded), Ctx(Tier.G, 3, day), ai)) defensiveMindedRests++;
        }
        Assert.True(aceDependentRests < defensiveMindedRests,
            $"豪腕依存({aceDependentRests})は守り勝つ({defensiveMindedRests})より温存頻度が低いはず。");
    }

    [Fact]
    public void FatigueLoad_IncreasesRestFrequency()
    {
        var ai = new EnemyAiCoefficients
        {
            AceRestBase = 0.05, AceRestTierGapWeight = 0, AceRestRoundsRemainingWeight = 0,
            AceRestFatigueWeight = 0.6, AceRestFatigueWindowDays = 7, AceRestFatigueReferencePitches = 100,
            AceRestFloor = 0.0, AceRestCap = 0.9,
        };
        var freshRests = 0;
        var tiredRests = 0;
        const int n = 400;
        const int matchDay = 10;
        for (var id = 1; id <= n; id++)
        {
            var school = Sch(id, 90);
            var freshLedger = new TournamentPitchLedger();
            var tiredLedger = new TournamentPitchLedger();
            tiredLedger.Record(PitcherLedgerKey.ForOpponent(school.Id, AceRestSelector.AceUniformNumber), pitches: 130, matchDay: 5);

            if (AceRestSelector.ShouldRestAce(school, Ctx(Tier.G, 3, matchDay, freshLedger), ai)) freshRests++;
            if (AceRestSelector.ShouldRestAce(school, Ctx(Tier.G, 3, matchDay, tiredLedger), ai)) tiredRests++;
        }
        Assert.True(tiredRests > freshRests,
            $"消耗済みエース({tiredRests})はフレッシュなエース({freshRests})より温存頻度が高いはず。");
    }

    [Fact]
    public void TierGap_LargerGap_IncreasesRestFrequency()
    {
        var ai = new EnemyAiCoefficients
        {
            AceRestBase = 0.05, AceRestTierGapWeight = 0.15, AceRestRoundsRemainingWeight = 0, AceRestFatigueWeight = 0,
            AceRestFloor = 0.0, AceRestCap = 0.95,
        };
        var school = Sch(1, 90);   // 自校はS級(7)
        var closeMatchRests = 0;
        var lopsidedRests = 0;
        const int n = 400;
        for (var day = 10; day < 10 + n; day++)
        {
            if (AceRestSelector.ShouldRestAce(school, Ctx(Tier.S, 3, day), ai)) closeMatchRests++;   // 相手も同格
            if (AceRestSelector.ShouldRestAce(school, Ctx(Tier.G, 3, day), ai)) lopsidedRests++;      // 相手は最弱
        }
        Assert.True(lopsidedRests > closeMatchRests,
            $"格下相手({lopsidedRests})は同格相手({closeMatchRests})より温存頻度が高いはず。");
    }
}

using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 入部の条件（約束）の出し方（設計書04 §2.4「入部の条件（約束）」）。たまにしか出ないこと、
/// 実力に見合うこと、進路希望と矛盾しないこと、約束すれば口説きやすくなること、を固定する。
/// </summary>
public sealed class ScoutPromiseTests
{
    private static readonly ScoutCoefficients C = new();
    private static readonly TeamStrengthCoefficients Strength = new();
    private static readonly ScoutPromiseKind[] None = System.Array.Empty<ScoutPromiseKind>();

    // ── 実力に見合う ───────────────────────────────────────

    [Fact]
    public void WeakCandidate_NeverAsksForStarterTreatment()
    {
        var kinds = Kinds(Candidate(level: 25, CareerPathPreference.Local), Roster(level: 60));
        Assert.DoesNotContain(ScoutPromiseKind.Regular, kinds);
        Assert.DoesNotContain(ScoutPromiseKind.Cleanup, kinds);
        Assert.DoesNotContain(ScoutPromiseKind.TopOfOrder, kinds);
        Assert.DoesNotContain(ScoutPromiseKind.SingleDigitNumber, kinds);
        Assert.Contains(ScoutPromiseKind.RestFirstSummer, kinds);
    }

    [Fact]
    public void StrongCandidate_AsksForStarterTreatment_NotForRest()
    {
        var kinds = Kinds(Candidate(level: 80, CareerPathPreference.Local), Roster(level: 40));
        Assert.Contains(ScoutPromiseKind.Regular, kinds);
        Assert.Contains(ScoutPromiseKind.Cleanup, kinds);
        Assert.DoesNotContain(ScoutPromiseKind.RestFirstSummer, kinds);
    }

    [Fact]
    public void PitchingConditions_OnlyWhenPitchingIsTheBestAxis()
    {
        var hitter = Candidate(level: 40, CareerPathPreference.Local);
        hitter.Player.SetLevel(AbilityKind.Contact, 90);
        hitter.Player.SetLevel(AbilityKind.Power, 90);
        var kinds = Kinds(hitter, Roster(level: 40));
        Assert.DoesNotContain(ScoutPromiseKind.StartingPitcher, kinds);
        Assert.DoesNotContain(ScoutPromiseKind.NoConsecutiveDays, kinds);

        var arm = Candidate(level: 40, CareerPathPreference.Local);
        arm.Player.SetLevel(AbilityKind.Velocity, 95);
        arm.Player.SetLevel(AbilityKind.Control, 95);
        arm.Player.SetLevel(AbilityKind.Stamina, 95);
        Assert.Contains(ScoutPromiseKind.StartingPitcher, Kinds(arm, Roster(level: 40)));
    }

    // ── 進路希望と矛盾しない ─────────────────────────────────

    [Fact]
    public void PlayingTimeSeeker_NeverAsksToSitOutTheFirstSummer()
    {
        var p = Candidate(level: 25, CareerPathPreference.PlayingTime);
        Assert.DoesNotContain(ScoutPromiseKind.RestFirstSummer, Kinds(p, Roster(level: 60)));
    }

    [Fact]
    public void PlayingTimeSeeker_WeighsPlayingConditionsAboveNeutralOnes()
    {
        var p = Candidate(level: 80, CareerPathPreference.PlayingTime);
        var options = ScoutPromises.Options(p, Roster(level: 40), hasDorm: true, None, Strength, C);
        var regular = options.Single(o => o.Kind == ScoutPromiseKind.Regular).Weight;
        var noBunt = options.Single(o => o.Kind == ScoutPromiseKind.NoBunt).Weight;
        Assert.True(regular > noBunt, $"{regular} / {noBunt}");
    }

    [Fact]
    public void Dorm_OnlyFromOutsideThePrefecture_AndOnlyWithoutADorm()
    {
        var far = Candidate(level: 50, CareerPathPreference.Prestige);
        far.Distance = RecruitDistance.Far;
        Assert.Contains(ScoutPromiseKind.Dorm, Kinds(far, Roster(level: 50), hasDorm: false));
        Assert.DoesNotContain(ScoutPromiseKind.Dorm, Kinds(far, Roster(level: 50), hasDorm: true));

        var home = Candidate(level: 50, CareerPathPreference.Prestige);
        home.Distance = RecruitDistance.SamePrefecture;
        Assert.DoesNotContain(ScoutPromiseKind.Dorm, Kinds(home, Roster(level: 50), hasDorm: false));
    }

    [Fact]
    public void ExclusiveConditions_AreNotOfferedTwice()
    {
        var p = Candidate(level: 80, CareerPathPreference.Local);
        var taken = new[] { ScoutPromiseKind.Cleanup };
        var kinds = ScoutPromises.Options(p, Roster(level: 40), true, taken, Strength, C).Select(o => o.Kind).ToList();
        Assert.DoesNotContain(ScoutPromiseKind.Cleanup, kinds);
        Assert.Contains(ScoutPromiseKind.Regular, kinds);   // 排他でない条件は重なってよい
    }

    // ── たまにしか出ない・一度きり ─────────────────────────────

    [Fact]
    public void Ask_HappensOnlyOccasionally()
    {
        var asked = 0;
        const int n = 2000;
        for (var i = 0; i < n; i++)
        {
            var p = Candidate(level: 60, CareerPathPreference.Local);
            if (ScoutPromises.TryAsk(p, Roster(level: 50), true, None, Strength, C, new Xoshiro256Random((ulong)(900000 + i)))
                != ScoutPromiseKind.None) asked++;
        }
        var rate = (double)asked / n;
        Assert.InRange(rate, C.PromiseAskProb - 0.04, C.PromiseAskProb + 0.04);
        Assert.True(C.PromiseAskProb < 0.5, "毎回のように出る設定になっている");
    }

    [Fact]
    public void Ask_IsOncePerCandidate_AndDeterministic()
    {
        ScoutCandidate AskUntilAsked(out ulong seed)
        {
            for (seed = 1; ; seed++)
            {
                var p = Candidate(level: 60, CareerPathPreference.Local);
                if (ScoutPromises.TryAsk(p, Roster(level: 50), true, None, Strength, C, new Xoshiro256Random(seed))
                    != ScoutPromiseKind.None) return p;
            }
        }

        var a = AskUntilAsked(out var seedA);
        var again = Candidate(level: 60, CareerPathPreference.Local);
        ScoutPromises.TryAsk(again, Roster(level: 50), true, None, Strength, C, new Xoshiro256Random(seedA));
        Assert.Equal(a.Promise, again.Promise);

        // もう条件を出した子は、何度訪問しても二度と出さない。
        for (ulong s = 1; s < 200; s++)
            Assert.Equal(ScoutPromiseKind.None,
                ScoutPromises.TryAsk(a, Roster(level: 50), true, None, Strength, C, new Xoshiro256Random(s)));
    }

    // ── 約束の効き ─────────────────────────────────────────

    [Fact]
    public void Accepting_RaisesTheProbability_DecliningDoesNotLowerIt()
    {
        var p = Candidate(level: 60, CareerPathPreference.Prestige);
        var before = ScoutModel.SuccessProbability(p, 30, Tier.C, false, 0.5, C);

        p.Promise = ScoutPromiseKind.Regular;
        ScoutPromises.Answer(p, accept: false);
        Assert.Equal(before, ScoutModel.SuccessProbability(p, 30, Tier.C, false, 0.5, C), 9);

        ScoutPromises.Answer(p, accept: true);
        Assert.Equal(before + C.PromiseBonus, ScoutModel.SuccessProbability(p, 30, Tier.C, false, 0.5, C), 9);
    }

    [Fact]
    public void Resolve_CarriesAnAcceptedPromiseOntoThePlayer()
    {
        var state = new ScoutingState { YearIndex = 2, Slots = 2 };
        var kept = Candidate(level: 60, CareerPathPreference.Prestige);
        kept.Targeted = true;
        kept.Promise = ScoutPromiseKind.Regular;
        kept.Promised = true;
        var declined = Candidate(level: 60, CareerPathPreference.Prestige);
        declined.Targeted = true;
        declined.Promise = ScoutPromiseKind.Captain;
        state.Candidates.Add(kept);
        state.Candidates.Add(declined);

        var sure = C with { ProbabilityMin = 1.0, ProbabilityMax = 1.0 };
        ScoutModel.Resolve(state, 50, Tier.C, false, Roster(level: 50), Strength, sure, new Xoshiro256Random(7));

        Assert.Equal(ScoutPromiseKind.Regular, kept.Player.Promise);
        Assert.Equal(ScoutPromiseStatus.Active, kept.Player.PromiseStatus);
        Assert.Equal(ScoutPromiseKind.None, declined.Player.Promise);
    }

    [Fact]
    public void Promise_SurvivesSaveLoad()
    {
        var state = new ScoutingState { YearIndex = 2, Slots = 2 };
        var p = Candidate(level: 60, CareerPathPreference.Prestige);
        p.Promise = ScoutPromiseKind.NoBunt;
        p.Promised = true;
        p.Player.Promise = ScoutPromiseKind.NoBunt;
        p.Player.PromiseStatus = ScoutPromiseStatus.Active;
        state.Candidates.Add(p);

        var back = KokoSim.Engine.Saves.ScoutingSaves.FromJson(KokoSim.Engine.Saves.ScoutingSaves.ToJson(state))!;
        Assert.Equal(ScoutPromiseKind.NoBunt, back.Candidates[0].Promise);
        Assert.True(back.Candidates[0].Promised);
        Assert.Equal(ScoutPromiseKind.NoBunt, back.Candidates[0].Player.Promise);
        Assert.Equal(ScoutPromiseStatus.Active, back.Candidates[0].Player.PromiseStatus);
    }

    [Fact]
    public void EveryKind_HasALabel()
    {
        foreach (ScoutPromiseKind k in System.Enum.GetValues(typeof(ScoutPromiseKind)))
            if (k != ScoutPromiseKind.None) Assert.False(string.IsNullOrEmpty(ScoutPromises.Label(k)), k.ToString());
    }

    // ── 補助 ────────────────────────────────────────────────

    private static List<ScoutPromiseKind> Kinds(ScoutCandidate p, IReadOnlyList<DevelopingPlayer> roster, bool hasDorm = true)
        => ScoutPromises.Options(p, roster, hasDorm, None, Strength, C).Select(o => o.Kind).ToList();

    private static ScoutCandidate Candidate(int level, CareerPathPreference pref)
    {
        var player = new DevelopingPlayer { Name = "候補" };
        foreach (var k in AbilityKinds.All) player.SetLevel(k, level);
        return new ScoutCandidate
        {
            Player = player, Preference = pref, Distance = RecruitDistance.SameRegion, RivalTier = Tier.C,
        };
    }

    private static List<DevelopingPlayer> Roster(int level)
    {
        var list = new List<DevelopingPlayer>();
        for (var i = 0; i < 20; i++)
        {
            var p = new DevelopingPlayer { Name = "部員" + i };
            foreach (var k in AbilityKinds.All) p.SetLevel(k, level);
            list.Add(p);
        }
        return list;
    }
}

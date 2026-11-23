using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 自校の怪物（issue #1468・2026-11-23 オーナー判断）。名声と関係なく ①たまに無条件で新入生に混じる
/// ②スカウトの候補（中学生）に混じり、勧誘に成功すると入部する。どちらも専用Fork＝ほかの抽選を動かさない。
/// AI校の怪物の率は変えない。
/// </summary>
public sealed class OwnPhenomTests
{
    private static RosterCoefficients WithRates(double intake, double scout) => new()
    {
        Phenom = new PhenomCoefficients { OwnIntakeRatePerYear = intake, ScoutCandidateRate = scout },
    };

    private static readonly RosterCoefficients Always = WithRates(1.0, 1.0);
    private static readonly RosterCoefficients Never = WithRates(0.0, 0.0);

    [Fact]
    public void AiRates_AreUnchanged()
    {
        // AI校の怪物（全4000校一様・年0.038%＋総合型0.008%）は今回の対象外＝既定のまま。
        var pc = new PersistentRosterCoefficients().Phenom;
        Assert.Equal(0.00038, pc.SpikeRatePerSchoolYear);
        Assert.Equal(0.00008, pc.AllRoundRatePerSchoolYear);
        // 自校の生成が読む係数は AI校と同じ値（Q20 決定6: 置き場は共通）。
        Assert.Equal(pc, new RosterCoefficients().Phenom);
    }

    [Fact]
    public void Apply_RaisesMainAbilityToSBand_AndKeepsHeadroom()
    {
        var p = ProspectGenerator.Intake(1, Never, new Xoshiro256Random(5), fixedCount: 1)[0];
        var before = p.Level(AbilityKind.Power);
        ProspectPhenom.Apply(p, PhenomType.Slugger, Never.Phenom, new Xoshiro256Random(6));
        Assert.Equal(PhenomType.Slugger, p.Phenom);
        Assert.InRange(p.Level(AbilityKind.Power), System.Math.Max(before, Never.Phenom.MainMin), 99);
        Assert.True(p.Cap(AbilityKind.Power) >= p.Level(AbilityKind.Power));
        Assert.True(p.Level(AbilityKind.Contact) >= Never.Phenom.SupportMin);
    }

    // ── ① 受動枠の新入生 ──────────────────────────────

    [Fact]
    public void YearlyIntake_MixesInOnePhenom_AtTheFront()
    {
        var roster = ProspectGenerator.Intake(1, Never, new Xoshiro256Random(11)).ToList();
        var withPhenom = RosterLifecycle.ConfirmIncoming(roster, 2, Always, new Xoshiro256Random(12));
        Assert.Equal(1, withPhenom.Count(p => p.Phenom != PhenomType.None));
        // スカウトの成功は受動枠の末尾と差し替わる＝怪物は先頭に置いて押し出されないようにする。
        Assert.NotEqual(PhenomType.None, withPhenom[0].Phenom);

        var none = RosterLifecycle.ConfirmIncoming(roster, 2, Never, new Xoshiro256Random(12));
        Assert.All(none, p => Assert.Equal(PhenomType.None, p.Phenom));
        // 同じ顔ぶれ（専用Fork＝人数・名前・ほかの抽選は動かない）。
        Assert.Equal(none.Select(p => p.Name).OrderBy(n => n), withPhenom.Select(p => p.Name).OrderBy(n => n));
        // 怪物以外の選手は1ビットも変わらない。
        foreach (var p in withPhenom.Where(x => x.Phenom == PhenomType.None))
        {
            var q = none.Single(x => x.Name == p.Name);
            foreach (var k in AbilityKinds.All) Assert.Equal(q.Level(k), p.Level(k));
        }
    }

    [Fact]
    public void YearlyIntake_IsDeterministic()
    {
        var c = WithRates(0.5, 0.0);
        for (var y = 1; y <= 20; y++)
        {
            var a = ProspectGenerator.Intake(y, c, new Xoshiro256Random((ulong)(900 + y)), rollPhenom: true);
            var b = ProspectGenerator.Intake(y, c, new Xoshiro256Random((ulong)(900 + y)), rollPhenom: true);
            Assert.Equal(a.Select(p => (p.Name, p.Phenom)), b.Select(p => (p.Name, p.Phenom)));
        }
    }

    [Fact]
    public void InitialRosterAndFixtures_DoNotRollPhenom()
    {
        // 新規ゲームの初期部員・試合用の素体（rollPhenom 既定 false）には混ぜない＝「新入生に混じる」だけ。
        for (var s = 0; s < 50; s++)
            Assert.All(ProspectGenerator.Intake(1, Always, new Xoshiro256Random((ulong)s)),
                p => Assert.Equal(PhenomType.None, p.Phenom));
    }

    [Fact]
    public void PitcherTypes_GoOnlyToPitcherLeaningPlayers()
    {
        var c = Always;
        for (var y = 1; y <= 200; y++)
        {
            var cohort = ProspectGenerator.Intake(y, c, new Xoshiro256Random((ulong)(4000 + y)), rollPhenom: true);
            foreach (var p in cohort.Where(x => x.Phenom != PhenomType.None))
            {
                if (PhenomPackages.IsPitcherType(p.Phenom)) Assert.True(PitcherRoleResolver.LeansPitcher(p, c));
                if (p.Phenom is PhenomType.Slugger or PhenomType.Speedster) Assert.False(PitcherRoleResolver.LeansPitcher(p, c));
            }
        }
    }

    // ── ② スカウトの候補 ──────────────────────────────

    private static ScoutingState Scout(RosterCoefficients rc, int seed) => ScoutModel.BuildCandidates(
        yearIndex: 3, field: ScoutField.Omakase, level: 3, slots: 4, homePrefectureId: 13, homeTier: Tier.C,
        talentCenter: rc.TalentCenterDefault, contractQualityAdd: 6, roster: rc, c: new ScoutCoefficients(),
        strengthCoeff: new TeamStrengthCoefficients(), rng: new Xoshiro256Random((ulong)seed));

    [Fact]
    public void ScoutCandidates_CanBePhenoms_WithoutDisturbingOtherRolls()
    {
        var on = Scout(Always, 31);
        var off = Scout(Never, 31);
        Assert.All(on.Candidates, x => Assert.NotEqual(PhenomType.None, x.Player.Phenom));
        Assert.All(off.Candidates, x => Assert.Equal(PhenomType.None, x.Player.Phenom));
        Assert.Equal(off.Candidates.Select(x => (x.Player.Name, x.PrefectureId, x.RivalTier, x.Preference)),
            on.Candidates.Select(x => (x.Player.Name, x.PrefectureId, x.RivalTier, x.Preference)));
    }

    [Fact]
    public void ScoutedPhenom_JoinsWhenRecruited()
    {
        var state = Scout(Always, 41);
        var target = state.Candidates[0];
        target.Targeted = true;
        target.Visits = 10;
        var sc = new ScoutCoefficients { ProbabilityMin = 1.0, ProbabilityMax = 1.0 };
        var won = ScoutModel.Resolve(state, 50, Tier.C, false, System.Array.Empty<DevelopingPlayer>(),
            new TeamStrengthCoefficients(), sc, new Xoshiro256Random(1));
        Assert.Single(won);
        Assert.NotEqual(PhenomType.None, won[0].Player.Phenom);
    }

    // ── セーブ・投影 ──────────────────────────────

    [Fact]
    public void Save_RoundTripsPhenom_AndOldSavesReadNone()
    {
        var p = ProspectGenerator.Intake(1, Never, new Xoshiro256Random(77), fixedCount: 1)[0];
        var plain = DevelopingPlayerSaves.ToJson(p);
        Assert.False(plain.Has("phenom"));   // 怪物でない選手にキーを足さない＝旧セーブと同じ形
        Assert.Equal(PhenomType.None, DevelopingPlayerSaves.FromJson(plain).Phenom);

        p.Phenom = PhenomType.Ace;
        Assert.Equal(PhenomType.Ace, DevelopingPlayerSaves.FromJson(DevelopingPlayerSaves.ToJson(p)).Phenom);
        Assert.Equal(PhenomType.Ace, p.Clone().Phenom);
    }

    [Fact]
    public void Projection_CarriesPhenomToMatchPlayer()
    {
        var p = ProspectGenerator.Intake(1, Never, new Xoshiro256Random(88), fixedCount: 1)[0];
        p.Phenom = PhenomType.Speedster;
        Assert.Equal(PhenomType.Speedster, RosterTeamBuilder.ToPlayer(p, FieldPosition.CenterField).Phenom);
    }
}

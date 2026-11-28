using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 投手の素質の見出し（issue #1523）: 隠しの投手センスと見える投手能力の差で、見立て→外部コーチの2段。
/// 肩書き（投手か野手か）は見ない。見出されると投手系の伸びの倍率が上がり、才能上限が引き上がる。
/// </summary>
public sealed class PitcherTalentTests
{
    private readonly ITestOutputHelper _out;
    public PitcherTalentTests(ITestOutputHelper output) => _out = output;

    private static readonly InsightCoefficients Ic = new();

    /// <summary>投手能力（球速・制球・キレ）の物理Lvをそろえた選手。投手センスは物理Lvで与える。</summary>
    private static DevelopingPlayer Make(int id, int sensePhys, int visiblePhys)
    {
        var p = new DevelopingPlayer { Id = id, PitcherSense = AbilityScale.Disp(sensePhys) };
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, 20); p.SetCap(k, 60); }
        foreach (var k in new[] { AbilityKind.Velocity, AbilityKind.Control, AbilityKind.PitchRank })
            p.SetLevel(k, AbilityScale.Disp(visiblePhys));
        return p;
    }

    // --- 対象の判定: センスと見える投手能力の差だけ。役（投手か野手か）は見ない ---

    [Fact]
    public void Candidate_NeedsHighSense_AndALargeGapToTheVisibleAbility()
    {
        Assert.True(PitcherTalentModel.IsCandidate(Make(1, sensePhys: 55, visiblePhys: 20), Ic));
        // センスが低い（下限未満）。
        Assert.False(PitcherTalentModel.IsCandidate(Make(2, sensePhys: 38, visiblePhys: 10), Ic));
        // センスは高いが、見える投手能力がもう追いついている（差が小さい）＝すでに活躍している投手は対象外。
        Assert.False(PitcherTalentModel.IsCandidate(Make(3, sensePhys: 55, visiblePhys: 50), Ic));
        // 見立て済み・見出し済みは対象外（二重に起きない）。
        var hinted = Make(4, 55, 20);
        hinted.PitcherTalent = PitcherTalentStage.Hinted;
        Assert.False(PitcherTalentModel.IsCandidate(hinted, Ic));
    }

    [Fact]
    public void Candidate_DoesNotLookAtTheRole_StagnantPitcherQualifiesToo()
    {
        // 投手として使っている選手（投手適性が最良）でも、伸び悩んでいれば（差が大きければ）対象になる。
        // issue #1792: 役の最終判定は実際の能力ベースへ移った。見える投手能力（visiblePhys=20）が
        // 他の能力（20）より表示Lvで低い（AbilityScale.Disp が −Δ するため）のは、まさにこのテストが
        // 言う「伸び悩み」そのもの＝能力ベースでは「投手として振るわない」側に転ぶ個体で構わない
        // （適性のSeedAptitudeForRoleは「見出し判定が役を見ない」ことの前提を示す材料としてだけ残す）。
        var pitcher = Make(1, 55, 20);
        PitcherRoleResolver.SeedAptitudeForRole(pitcher, true);
        var fielder = Make(2, 55, 20);
        PitcherRoleResolver.SeedAptitudeForRole(fielder, false);
        Assert.True(PitcherTalentModel.IsCandidate(pitcher, Ic));
        Assert.True(PitcherTalentModel.IsCandidate(fielder, Ic));
    }

    // --- 2段の進み方 ---

    [Fact]
    public void Week_HintsFirst_ThenDiscovers_NeverBothInOneWeek()
    {
        var c = new InsightCoefficients { PitcherTalentHintProb = 1.0, PitcherTalentDiscoverProb = 1.0 };
        var p = Make(1, 55, 20);
        var roster = new List<DevelopingPlayer> { p };

        var w1 = PitcherTalentModel.Week(roster, new Xoshiro256Random(1), c);
        Assert.Single(w1);
        Assert.Equal(PitcherTalentStage.Hinted, w1[0].Stage);
        Assert.Equal(PitcherTalentStage.Hinted, p.PitcherTalent);

        var w2 = PitcherTalentModel.Week(roster, new Xoshiro256Random(1), c);
        Assert.Single(w2);
        Assert.Equal(PitcherTalentStage.Discovered, w2[0].Stage);
        Assert.Equal(PitcherTalentStage.Discovered, p.PitcherTalent);

        Assert.Empty(PitcherTalentModel.Week(roster, new Xoshiro256Random(1), c));
    }

    [Fact]
    public void Week_NoOneIsTouchedWhenNobodyQualifies()
    {
        var roster = Enumerable.Range(0, 50).Select(i => Make(i + 1, 30, 30)).ToList();
        var c = new InsightCoefficients { PitcherTalentHintProb = 1.0 };
        Assert.Empty(PitcherTalentModel.Week(roster, new Xoshiro256Random(2), c));
        Assert.All(roster, p => Assert.Equal(PitcherTalentStage.None, p.PitcherTalent));
    }

    [Fact]
    public void Week_IsDeterministic_AndRareOverAYear()
    {
        List<PitcherTalentNotice> Run(ulong seed)
        {
            var roster = Enumerable.Range(0, 20).Select(i => Make(i + 1, 55, 20)).ToList();
            var rng = new Xoshiro256Random(seed);
            var all = new List<PitcherTalentNotice>();
            for (var w = 0; w < 52; w++) all.AddRange(PitcherTalentModel.Week(roster, rng, Ic));
            return all;
        }
        var a = Run(9);
        var b = Run(9);
        Assert.Equal(a.Select(n => (n.Player.Id, n.Stage)), b.Select(n => (n.Player.Id, n.Stage)));
        // 既定の確率: 候補20人が1年で大半は見立てが出る／見出しまで進むのは一部。毎週は起きない。
        var hints = a.Count(n => n.Stage == PitcherTalentStage.Hinted);
        var found = a.Count(n => n.Stage == PitcherTalentStage.Discovered);
        _out.WriteLine($"候補20人×52週: 見立て {hints} 見出し {found}");
        Assert.InRange(hints, 8, 20);
        Assert.InRange(found, 1, hints);
    }

    [Fact]
    public void Intake_ClassHasOnlyAFewCandidates_FromTheIndependentDraw()
    {
        // 生成の独立の乱数で、新入生の中に対象（高センス＋差が大きい）が数％だけ混ざる＝毎年の出来事の数が現実的。
        var all = Enumerable.Range(0, 30)
            .SelectMany(y => ProspectGenerator.Intake(y, new RosterCoefficients(), new Xoshiro256Random((ulong)(y + 7)))).ToList();
        var candidates = all.Count(p => PitcherTalentModel.IsCandidate(p, Ic));
        var share = candidates / (double)all.Count;
        _out.WriteLine($"新入生 {all.Count} 人中 対象 {candidates} 人（{share:P1}）");
        Assert.InRange(share, 0.005, 0.10);
    }

    // --- 効果: 見立てだけでは伸びず、見出されると伸びる ---

    private static (double Gain, double Cap) Train(DevelopingPlayer src, PitcherTalentStage stage)
    {
        var p = src.Clone();
        if (stage == PitcherTalentStage.Discovered) PitcherTalentModel.Discover(p, Ic);
        else p.PitcherTalent = stage;
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var before = p.Level(AbilityKind.Control);
        for (var w = 0; w < 60; w++) DevelopmentModel.TrainWeek(p, TrainingMenu.Pitching, 0, 1.0, stages, c);
        return (p.Level(AbilityKind.Control) - before, p.Cap(AbilityKind.Control));
    }

    [Fact]
    public void Hint_DoesNotChangeGrowth_Discovery_Does()
    {
        var src = Make(1, 55, 20);
        foreach (var k in AbilityKinds.All) src.SetCap(k, 99);
        var none = Train(src, PitcherTalentStage.None);
        var hinted = Train(src, PitcherTalentStage.Hinted);
        var found = Train(src, PitcherTalentStage.Discovered);
        _out.WriteLine($"制球の伸び: なし +{none.Gain} 見立て +{hinted.Gain} 見出し +{found.Gain}");
        Assert.Equal(none.Gain, hinted.Gain);
        Assert.True(found.Gain > none.Gain, "見出されると投手系が伸びやすい");
    }

    [Fact]
    public void Discovery_RaisesPitchingCaps_Only_AndStopsAt99()
    {
        var p = Make(1, 55, 20);
        p.SetCap(AbilityKind.Control, 95);
        PitcherTalentModel.Discover(p, Ic);
        Assert.Equal(PitcherTalentStage.Discovered, p.PitcherTalent);
        Assert.Equal(99, p.Cap(AbilityKind.Control));
        Assert.Equal(60 + Ic.PitcherTalentCapBonus, p.Cap(AbilityKind.Velocity));
        Assert.Equal(60, p.Cap(AbilityKind.Contact));
        Assert.Equal(60, p.Cap(AbilityKind.Fielding));
    }

    [Fact]
    public void GrowthFactor_DiscoveredHasAFloorOfTheAwakenedFactor_ButNeverLowersAnything()
    {
        var c = new TrainingCoefficients();
        Assert.Equal(c.PitcherAwakenedGrowthFactor, c.PitcherSenseGrowthFactor(AbilityScale.Disp(45), discovered: true), 9);
        Assert.Equal(1.0, c.PitcherSenseGrowthFactor(AbilityScale.Disp(45)), 9);
        Assert.True(c.PitcherAwakenedGrowthFactor > c.PitcherSenseGrowthMax);
        // 見出されていない選手の倍率は従来のまま（0.5〜1.0）。
        Assert.Equal(c.PitcherSenseGrowthMin, c.PitcherSenseGrowthFactor(1), 9);
    }

    /// <summary>
    /// 強い投手の伸びの目標（豪腕×名将＋フル施設で3年 +15〜20km/h・テスト上限 +28＝Heavy の Report_PowerArmProspect）を、
    /// 見出された選手が超えない。見出されるのは見える投手能力がまだ低い選手（才能上限も新入生の高め＝70）＝同じ名将＋フル施設の
    /// 3年でも、到達点は豪腕素質（球速Lv46・才能上限99）の通常の3年後を超えない。
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(26)]
    [InlineData(32)]
    public void DiscoveredProspect_WithAceAndFullFacility_NeverOutgrowsThePowerArmTarget(int startLevel)
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        int[] weeksPerStage = { 24, 24, 24, 24, 16 };
        var coaching = new CoachingProfile { Pitching = 90, Batting = 90, Defense = 90 };
        var tier = c.FacilityTiers[System.Math.Min(4, c.FacilityTiers.Count - 1)];
        var training = c with { FacilityCoef = tier.Coef };

        double FinalKmh(int start, bool discovered, int cap = 70)
        {
            var p = new DevelopingPlayer();
            PitcherRoleResolver.SeedAptitudeForRole(p, true);
            p.SetLevel(AbilityKind.Velocity, start);
            p.SetCap(AbilityKind.Velocity, cap);
            if (discovered) PitcherTalentModel.Discover(p, Ic);
            var alloc = new[] { new MenuAllocation(TrainingMenu.VelocityTraining, tier.BudgetMinutes) };
            for (var s = 0; s < weeksPerStage.Length; s++)
                for (var w = 0; w < weeksPerStage[s]; w++)
                    DevelopmentModel.TrainWeekPlan(p, alloc, training.ReferenceWeekMinutes, s, 1.0, stages, training, coaching: coaching);
            return PitcherAttributes.VelocityKmhFromLevel(p.Level(AbilityKind.Velocity));
        }
        var powerArm = FinalKmh(46, discovered: false, cap: 99);
        var found = FinalKmh(startLevel, discovered: true);
        var plain = FinalKmh(startLevel, discovered: false);
        _out.WriteLine($"開始Lv{startLevel}: 通常 {plain:F1}km/h ／ 見出された {found:F1}km/h ／ 豪腕素質（Lv46）の通常 {powerArm:F1}km/h");
        Assert.True(found > plain, "見出されると伸びる");
        Assert.True(found <= powerArm + 0.5, $"見出された選手が豪腕素質の通常の到達点を超えた（{found:F1} > {powerArm:F1}km/h）");
    }

    // --- セーブ ---

    [Fact]
    public void Save_RoundTripsTheStage_AndOldSavesReadAsNone()
    {
        var roster = new List<DevelopingPlayer> { Make(1, 55, 20), Make(2, 55, 20), Make(3, 55, 20) };
        roster[1].PitcherTalent = PitcherTalentStage.Hinted;
        roster[2].PitcherTalent = PitcherTalentStage.Discovered;
        var json = SaveCodec.Serialize(new SaveState { MasterSeed = 5, Roster = roster });
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);
        Assert.Equal(new[] { PitcherTalentStage.None, PitcherTalentStage.Hinted, PitcherTalentStage.Discovered },
            s.Roster.Select(p => p.PitcherTalent));

        // 旧版のセーブ（キーが無い）は「まだ何も起きていない」。
        var legacy = System.Text.RegularExpressions.Regex.Replace(json, "\"pitcherTalent\":\"[A-Za-z]+\",", "")
            .Replace($"\"version\":{SaveCodec.CurrentVersion}", "\"version\":44");
        Assert.DoesNotContain("pitcherTalent", legacy);
        Assert.True(SaveCodec.TryDeserialize(legacy, out var old, out var err2), err2);
        Assert.All(old.Roster, p => Assert.Equal(PitcherTalentStage.None, p.PitcherTalent));
    }

    [Fact]
    public void Clone_CarriesTheStage()
    {
        var p = Make(1, 55, 20);
        p.PitcherTalent = PitcherTalentStage.Discovered;
        Assert.Equal(PitcherTalentStage.Discovered, p.Clone().PitcherTalent);
    }
}

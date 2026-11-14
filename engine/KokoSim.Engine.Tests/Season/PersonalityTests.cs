using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 性格の4軸×16タイプ（設計書23 §1・issue #932）。
/// 生成（決定論・出やすさ・母集団平均）／軸の効き（50で恒等・端で旧表の幅）／16タイプの導出／
/// 旧セーブの移行／見極めの軸ごとの開示を検証する。
/// </summary>
public sealed class PersonalityTests
{
    private static readonly RosterCoefficients Roster = new();
    private static readonly PersonalityCoefficients PC = new();
    private readonly ITestOutputHelper _out;

    public PersonalityTests(ITestOutputHelper output) => _out = output;

    private static List<DevelopingPlayer> Cohort(int seeds = 400)
    {
        var all = new List<DevelopingPlayer>();
        for (var s = 0; s < seeds; s++)
            all.AddRange(ProspectGenerator.Intake(1, Roster, new Xoshiro256Random((ulong)(9000 + s))));
        return all;
    }

    // ===== 生成: 決定論・出やすさ =====

    [Fact]
    public void Intake_Personality_IsDeterministic()
    {
        var a = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random(31));
        var b = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random(31));
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Personality, b[i].Personality);
            Assert.Equal(a[i].Receptiveness, b[i].Receptiveness);
            Assert.Equal(a[i].Diligence, b[i].Diligence);
            Assert.Equal(a[i].Selflessness, b[i].Selflessness);
            Assert.Equal(a[i].Leadership, b[i].Leadership);
        }
    }

    [Fact]
    public void Intake_All16TypesAppear_NeverNormal()
    {
        var cohort = Cohort();
        var seen = cohort.Select(p => p.Personality).ToHashSet();
        foreach (var t in Personalities.All16)
            Assert.Contains(t, seen);
        Assert.DoesNotContain(Personality.Normal, seen);
    }

    /// <summary>
    /// 16タイプの分布の下限（Q44 決着 2026-11-14: どのタイプもそれぞれ3%以上は出る）。新入生約1万人で測る。
    /// 種の出やすさ（努力家・マイペースが多く天才肌は少ない）は残す。
    /// </summary>
    [Fact]
    public void Intake_EveryOneOf16Types_AppearsAtLeast3Percent()
    {
        var pop = new List<DevelopingPlayer>();
        for (var s = 0; pop.Count < 10_000; s++)
            pop.AddRange(ProspectGenerator.Intake(1, Roster, new Xoshiro256Random((ulong)(70_000 + s))));
        var counts = pop.GroupBy(p => p.Personality).ToDictionary(g => g.Key, g => g.Count());
        double Freq(Personality t) => counts.TryGetValue(t, out var n) ? (double)n / pop.Count : 0.0;
        foreach (var t in Personalities.All16) _out.WriteLine($"{Personalities.DisplayName(t)}: {Freq(t):P1}");
        foreach (var t in Personalities.All16)
            Assert.True(Freq(t) >= 0.03, $"{Personalities.DisplayName(t)} が {Freq(t):P1}（下限3%）");
        Assert.True(PC.Profile(Personality.Hardworker).SpawnWeight > PC.Profile(Personality.Genius).SpawnWeight);
    }

    // ===== 母集団平均（帯は選手の生成を通らない＝ここが安全網。設計書23 §1.4） =====

    /// <summary>旧8タイプ表（spawn_weight 加重）の母集団平均。</summary>
    private const double OldCoaching = 1.0085, OldGrowth = 1.0052, OldBunt = 0.0089, OldChance = 1.0134, OldLeadOffset = 0.8;

    private void AssertPopulationMeans(string label, IReadOnlyList<(int R, int D, int S)> pop)
    {
        var coaching = pop.Average(p => PC.CoachingReceptivity(p.R));
        var growth = pop.Average(p => PC.SelfGrowthFactor(p.D));
        var bunt = pop.Average(p => PC.BuntSuccessBonus(p.S));
        var chance = pop.Average(p => PC.ChanceHitFactor(p.S));
        _out.WriteLine($"{label} n={pop.Count}: 指導 {coaching:F4} 自主成長 {growth:F4} 犠打 {bunt:+0.0000;-0.0000} チャンス {chance:F4}");
        Assert.InRange(coaching, OldCoaching - 0.005, OldCoaching + 0.005);
        Assert.InRange(growth, OldGrowth - 0.005, OldGrowth + 0.005);
        Assert.InRange(bunt, OldBunt - 0.003, OldBunt + 0.003);
        Assert.InRange(chance, OldChance - 0.005, OldChance + 0.005);
    }

    [Fact]
    public void PopulationMeans_SampleAxes_10k_MatchOldTable()
    {
        var root = new Xoshiro256Random(0x5EED_2300UL);
        var pop = new List<(int, int, int)>(10_000);
        var leadOffset = 0.0;
        for (var i = 0; i < 10_000; i++)
        {
            var d = PC.SampleAxes(root.Fork((ulong)i));
            leadOffset += PC.Profile(d.SeedType).LeadershipMeanOffset;
            pop.Add((d.Receptiveness, d.Diligence, d.Selflessness));
        }
        AssertPopulationMeans("SampleAxes", pop);
        Assert.InRange(leadOffset / pop.Count, OldLeadOffset - 0.5, OldLeadOffset + 0.5);
    }

    [Fact]
    public void PopulationMeans_ProspectGenerator_10k_MatchOldTable()
    {
        var pop = new List<(int, int, int)>();
        for (var s = 0; pop.Count < 10_000; s++)
            foreach (var p in ProspectGenerator.Intake(1, Roster, new Xoshiro256Random((ulong)(70_000 + s))))
                pop.Add((p.Receptiveness, p.Diligence, p.Selflessness));
        AssertPopulationMeans("新入生", pop);
    }

    [Fact]
    public void PopulationMeans_StrengthTeamFactory_10k_MatchOldTable()
    {
        // AI校の選手（StrengthTeamFactory → AiRosterFactory の共通生成器）。バントと得点圏の焼き込みも軸の式と一致する。
        var pop = new List<(int, int, int)>(10_000);
        for (var i = 0; i < 10_000; i++)
        {
            var p = StrengthTeamFactory.PositionPlayer(FieldPosition.CenterField, 55, new Xoshiro256Random((ulong)(i + 1)),
                PC, (ulong)(i * 7919 + 13), "a");
            Assert.Equal(PC.BuntSuccessBonus(p.Selflessness), p.BuntSuccessBonus, 12);
            Assert.Equal(PC.ChanceHitFactor(p.Selflessness), p.ChanceHitFactor, 12);
            pop.Add((p.Receptiveness, p.Diligence, p.Selflessness));
        }
        AssertPopulationMeans("AI校", pop);
    }

    // ===== 自主成長の同タイプ内の個人差（Q47 決着 2026-11-14: 旧 PersonalityFactor の σ0.06 相当に戻す） =====

    /// <summary>タイプごとの自主成長の標準偏差を人数で重み付けした平均（同タイプ内のσ）と、母集団平均。</summary>
    private (double WithinSd, double Mean) SelfGrowthSpread(string label, IReadOnlyList<(Personality T, int D, double J)> pop)
    {
        var sumSq = 0.0;
        var n = 0;
        foreach (var g in pop.GroupBy(p => p.T))
        {
            var xs = g.Select(p => PC.SelfGrowthFactor(p.D, p.J)).ToList();
            var m = xs.Average();
            sumSq += xs.Sum(x => (x - m) * (x - m));
            n += xs.Count - 1;
        }
        var within = System.Math.Sqrt(sumSq / n);
        var mean = pop.Average(p => PC.SelfGrowthFactor(p.D, p.J));
        var axisOnly = pop.Average(p => PC.SelfGrowthFactor(p.D));
        _out.WriteLine($"{label} n={pop.Count}: 同タイプ内σ {within:F4} 平均 {mean:F4}（勤勉さだけ {axisOnly:F4}）");
        return (within, mean);
    }

    private static void AssertOldSpread((double WithinSd, double Mean) r)
    {
        Assert.InRange(r.WithinSd, 0.055, 0.065);              // 旧 PersonalityFactor のタイプ内ジッタ σ0.06 相当
        Assert.InRange(r.Mean, OldGrowth - 0.005, OldGrowth + 0.005); // 個人差は平均0＝母集団平均は旧表のまま
    }

    [Fact]
    public void SelfGrowth_WithinTypeSpread_MatchesOldJitter_Intake()
    {
        var pop = new List<(Personality, int, double)>();
        for (var s = 0; pop.Count < 10_000; s++)
            foreach (var p in ProspectGenerator.Intake(1, Roster, new Xoshiro256Random((ulong)(70_000 + s))))
                pop.Add((p.Personality, p.Diligence, p.SelfGrowthJitter));
        AssertOldSpread(SelfGrowthSpread("新入生", pop));
    }

    [Fact]
    public void SelfGrowth_WithinTypeSpread_MatchesOldJitter_AiSchools()
    {
        var pop = new List<(Personality, int, double)>(10_000);
        for (var i = 0; i < 10_000; i++)
        {
            var p = StrengthTeamFactory.PositionPlayer(FieldPosition.CenterField, 55, new Xoshiro256Random((ulong)(i + 1)),
                PC, (ulong)(i * 7919 + 13), "a");
            pop.Add((p.Personality, p.Diligence, p.SelfGrowthJitter));
        }
        AssertOldSpread(SelfGrowthSpread("AI校", pop));
    }

    [Fact]
    public void SelfGrowthJitter_DoesNotDisturbAxesOrMainStream()
    {
        // 個人差は性格の Fork の末尾で引く＝②③④の値は個人差が無いとき（σ0）と同じ、主RNGの列も同じ。
        var noJitter = PC with { SelfGrowthJitterSd = 0.0 };
        var a = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random(77), personalities: PC);
        var b = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random(77), personalities: noJitter);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal((a[i].Receptiveness, a[i].Diligence, a[i].Selflessness), (b[i].Receptiveness, b[i].Diligence, b[i].Selflessness));
            Assert.Equal(a[i].InjuryResistance, b[i].InjuryResistance);
            Assert.Equal(0.0, b[i].SelfGrowthJitter);
        }
        Assert.Contains(a, p => p.SelfGrowthJitter != 0.0);
    }

    [Fact]
    public void SelfGrowthJitter_CarriesThroughProjectionAndTransfer()
    {
        var dp = PersonalityFixture.Developing(Personality.Genius, selfGrowthJitter: 0.05);
        Assert.Equal(0.05, RosterTeamBuilder.ToPlayer(dp, FieldPosition.LeftField).SelfGrowthJitter, 12);
        Assert.Equal(0.05, dp.Clone().SelfGrowthJitter, 12);
        // 転任: 自校→AI校→自校の往復でも個人差は引き継ぐ（性格と同じく生まれつき・Q45/Q47）。
        var ai = KokoSim.Engine.Career.TransferRosterConversion.ToAiPlayer(dp, 3);
        Assert.Equal(0.05, ai.Snapshot.SelfGrowthJitter, 12);
        var back = KokoSim.Engine.Career.TransferRosterConversion.ToDevelopingPlayer(
            ai, new Xoshiro256Random(5), Roster, PC);
        Assert.Equal(0.05, back.SelfGrowthJitter, 12);
        Assert.Equal(dp.Diligence, back.Diligence);
    }

    [Fact]
    public void SelfGrowth_JitterRaisesGrowth()
    {
        int Grown(double jitter)
        {
            var p = new DevelopingPlayer { Diligence = 50, SelfGrowthJitter = jitter };
            p.SetLevel(AbilityKind.Contact, 30);
            p.SetCap(AbilityKind.Contact, 99);
            for (var w = 0; w < 30; w++)
                DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 1, 1.0, new GrowthStageTable(), new TrainingCoefficients());
            return p.Level(AbilityKind.Contact);
        }
        Assert.True(Grown(+0.10) > Grown(0.0));
        Assert.True(Grown(0.0) > Grown(-0.10));
    }

    // ===== 軸の効き（50で恒等・端で旧表の幅） =====

    [Fact]
    public void AxisEffects_AreIdentityAt50()
    {
        Assert.Equal(1.0, PC.CoachingReceptivity(50), 12);
        Assert.Equal(1.0, PC.SelfGrowthFactor(50), 12);
        Assert.Equal(0.0, PC.BuntSuccessBonus(50), 12);
        Assert.Equal(1.0, PC.ChanceHitFactor(50), 12);
        Assert.Equal(0.0, PC.MessengerBonus(50, true), 12);
        Assert.Equal(0.0, PC.MessengerBonus(50, false), 12);
    }

    [Fact]
    public void AxisEffects_EndsSpanOldTableRange()
    {
        // 端（1／99＝d∓0.98）で旧タイプ表の幅（指導 0.88〜1.10・自主成長 0.92〜1.10・犠打 −0.06〜+0.05・チャンス 0.97〜1.10）の98%。
        Assert.Equal(1.0 + 0.98 * 0.10, PC.CoachingReceptivity(99), 9);
        Assert.Equal(1.0 - 0.98 * 0.12, PC.CoachingReceptivity(1), 9);
        Assert.Equal(1.0 + 0.98 * 0.10, PC.SelfGrowthFactor(99), 9);
        Assert.Equal(1.0 - 0.98 * 0.08, PC.SelfGrowthFactor(1), 9);
        Assert.Equal(0.98 * 0.05, PC.BuntSuccessBonus(99), 9);
        Assert.Equal(-0.98 * 0.06, PC.BuntSuccessBonus(1), 9);
        Assert.Equal(1.0 - 0.98 * 0.03, PC.ChanceHitFactor(99), 9);
        Assert.Equal(1.0 + 0.98 * 0.10, PC.ChanceHitFactor(1), 9);
        // 伝令の得意場面: 献身は投手へ、目立ちたがりは打者へ。
        Assert.True(PC.MessengerBonus(90, true) > 0 && PC.MessengerBonus(90, false) < 0);
        Assert.True(PC.MessengerBonus(10, true) < 0 && PC.MessengerBonus(10, false) > 0);
    }

    [Fact]
    public void AxisEffects_AreMonotone()
    {
        for (var v = 1; v < 99; v++)
        {
            Assert.True(PC.CoachingReceptivity(v + 1) > PC.CoachingReceptivity(v));
            Assert.True(PC.SelfGrowthFactor(v + 1) > PC.SelfGrowthFactor(v));
            Assert.True(PC.BuntSuccessBonus(v + 1) > PC.BuntSuccessBonus(v));
            Assert.True(PC.ChanceHitFactor(v + 1) < PC.ChanceHitFactor(v));
        }
    }

    // ===== 16タイプの導出（設計書23 §1.3） =====

    [Theory]
    [InlineData(70, 70, 70, 70, Personality.HonorStudent)]
    [InlineData(70, 70, 70, 30, Personality.Star)]
    [InlineData(70, 70, 30, 70, Personality.Caretaker)]
    [InlineData(70, 70, 30, 30, Personality.PartyAnimal)]
    [InlineData(70, 30, 70, 70, Personality.HotBlood)]
    [InlineData(70, 30, 70, 30, Personality.GangLeader)]
    [InlineData(70, 30, 30, 70, Personality.BigBrother)]
    [InlineData(70, 30, 30, 30, Personality.Rowdy)]
    [InlineData(30, 70, 70, 70, Personality.Hardworker)]
    [InlineData(30, 70, 70, 30, Personality.Ambitious)]
    [InlineData(30, 70, 30, 70, Personality.Introvert)]
    [InlineData(30, 70, 30, 30, Personality.MoodMaker)]
    [InlineData(30, 30, 70, 70, Personality.Craftsman)]
    [InlineData(30, 30, 70, 30, Personality.Genius)]
    [InlineData(30, 30, 30, 70, Personality.MyPace)]
    [InlineData(30, 30, 30, 30, Personality.LoneWolf)]
    [InlineData(80, 50, 50, 50, Personality.Normal)]
    [InlineData(50, 50, 50, 49, Personality.Star)]
    public void Classify_FollowsDesignTable(int l, int r, int d, int s, Personality expected)
    {
        Assert.Equal(expected, Personalities.Classify(l, r, d, s));
        if (expected != Personality.Normal)
        {
            Assert.Equal(Personalities.High(l), Personalities.CellHigh(expected, PersonalityAxis.Leadership));
            Assert.Equal(Personalities.High(s), Personalities.CellHigh(expected, PersonalityAxis.Selflessness));
        }
    }

    [Fact]
    public void All16_HaveDistinctNames_AndSeedRepresentativesSitInTheirCell()
    {
        Assert.Equal(16, Personalities.All16.Select(Personalities.DisplayName).Distinct().Count());
        foreach (var t in Personalities.Spawnable)
        {
            var pp = PC.Profile(t);
            Assert.Equal(t, Personalities.Classify(PersonalityFixture.Leadership(t), pp.Receptiveness, pp.Diligence, pp.Selflessness));
        }
    }

    // ===== 効きの配線 =====

    [Fact]
    public void Coaching_ReceptiveBeatsStubborn_NullIsNeutral()
    {
        double Gain(Personality type, PersonalityCoefficients? pc)
        {
            var c = new TrainingCoefficients();
            var stages = new GrowthStageTable();
            var p = PersonalityFixture.Developing(type);
            p.SetLevel(AbilityKind.Contact, 30);
            p.SetCap(AbilityKind.Contact, 99);
            var before = p.Level(AbilityKind.Contact) + p.Exp(AbilityKind.Contact) / 100.0;
            for (var w = 0; w < 30; w++)
                DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 1, 1.0, stages, c, personalities: pc);
            return (p.Level(AbilityKind.Contact) + p.Exp(AbilityKind.Contact) / 100.0) - before;
        }

        // 素直さだけ違う2人（勤勉さは同じ）で比べる。
        double GainR(int receptiveness)
        {
            var c = new TrainingCoefficients();
            var p = new DevelopingPlayer { Receptiveness = receptiveness };
            p.SetLevel(AbilityKind.Contact, 30);
            p.SetCap(AbilityKind.Contact, 99);
            for (var w = 0; w < 30; w++)
                DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 1, 1.0, new GrowthStageTable(), c, personalities: PC);
            return p.Level(AbilityKind.Contact) + p.Exp(AbilityKind.Contact) / 100.0;
        }
        Assert.True(GainR(90) > GainR(10));
        // personalities=null は素直さを見ない（従来の呼び出しと一致）。勤勉さは既定の式で常に効く。
        Assert.Equal(Gain(Personality.Normal, PC), Gain(Personality.Normal, null), 6);
    }

    [Fact]
    public void SelfGrowth_DiligentGrowsFaster()
    {
        int Grown(int diligence)
        {
            var p = new DevelopingPlayer { Diligence = diligence };
            p.SetLevel(AbilityKind.Contact, 30);
            p.SetCap(AbilityKind.Contact, 99);
            for (var w = 0; w < 30; w++)
                DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 1, 1.0, new GrowthStageTable(), new TrainingCoefficients());
            return p.Level(AbilityKind.Contact);
        }
        _out.WriteLine($"{Grown(10)} {Grown(50)} {Grown(90)}");
        Assert.True(Grown(90) > Grown(50));
        Assert.True(Grown(50) > Grown(10));
    }

    [Fact]
    public void Bunt_SelflessRaises_SpotlightLowers()
    {
        var c = new BaserunningCoefficients();
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 140 };
        Player Batter(double bonus) => new() { Bunt = 50, BuntSuccessBonus = bonus };

        var selfless = BuntResolver.SuccessRate(Batter(+0.05), pitcher, courseDifficulty: 0.0, c);
        var neutral = BuntResolver.SuccessRate(Batter(0.0), pitcher, courseDifficulty: 0.0, c);
        var spotlight = BuntResolver.SuccessRate(Batter(-0.06), pitcher, courseDifficulty: 0.0, c);

        Assert.True(selfless > neutral);
        Assert.True(spotlight < neutral);
        Assert.Equal(0.05, selfless - neutral, 6);
    }

    [Fact]
    public void Projection_BakesChanceHitAndBuntScalarsFromSelflessness()
    {
        var dp = PersonalityFixture.Developing(Personality.MoodMaker);
        var player = RosterTeamBuilder.ToPlayer(dp, FieldPosition.LeftField, personalityCoeff: PC);
        Assert.Equal(Personality.MoodMaker, player.Personality);
        Assert.Equal(dp.Receptiveness, player.Receptiveness);
        Assert.Equal(dp.Diligence, player.Diligence);
        Assert.Equal(PC.ChanceHitFactor(dp.Selflessness), player.ChanceHitFactor, 9);
        Assert.Equal(PC.BuntSuccessBonus(dp.Selflessness), player.BuntSuccessBonus, 9);
        Assert.True(player.ChanceHitFactor > 1.0); // 目立ちたがりは得点圏強攻で上振れ
    }

    [Fact]
    public void Projection_Default_IsNeutral()
    {
        var dp = new DevelopingPlayer();
        var player = RosterTeamBuilder.ToPlayer(dp, FieldPosition.CenterField);
        Assert.Equal(Personality.Normal, player.Personality);
        Assert.Equal(0.0, player.BuntSuccessBonus, 9);
        Assert.Equal(1.0, player.ChanceHitFactor, 9);
    }

    // ===== 旧セーブの移行（設計書23 §1.5） =====

    /// <summary>今の部員の JSON から軸のキーを抜き、v26 の形（personality＋personalityFactor）にする。</summary>
    private static JsonObject AsLegacy(JsonObject now, Personality legacy, double factor)
    {
        var o = new JsonObject();
        foreach (var k in now.Keys)
        {
            if (k is "receptiveness" or "diligence" or "selflessness" or "persKnown" or "persHigh" or "growthJitter") continue;
            o.Add(k, now[k]);
        }
        o.Add("personality", legacy.ToString());
        o.Add("personalityFactor", JsonValue.Of(factor));
        return o;
    }

    [Theory]
    [InlineData(Personality.HotBlood, 1.08)]
    [InlineData(Personality.Hardworker, 1.10)]
    [InlineData(Personality.Genius, 1.10)]
    [InlineData(Personality.HonorStudent, 1.00)]
    [InlineData(Personality.MyPace, 0.95)]
    [InlineData(Personality.MoodMaker, 0.92)]
    [InlineData(Personality.LoneWolf, 1.00)]
    [InlineData(Personality.Introvert, 0.95)]
    // タイプ内ジッタで中心から外れた旧個体（勤勉さのマスをまたぐ値）でも、タイプ名と自主成長の効きをどちらも保つ。
    [InlineData(Personality.Genius, 0.97)]
    [InlineData(Personality.MoodMaker, 1.04)]
    [InlineData(Personality.Hardworker, 1.22)]
    public void LegacySave_DevelopingPlayer_MigratesToRepresentative_AndKeepsTypeName(Personality legacy, double factor)
    {
        var now = new DevelopingPlayer { Name = "旧", Leadership = PersonalityFixture.Leadership(legacy) };
        var json = AsLegacy(DevelopingPlayerSaves.ToJson(now), legacy, factor);
        var a = DevelopingPlayerSaves.FromJson(json);
        var b = DevelopingPlayerSaves.FromJson(json); // 乱数不使用＝読むたびに同じ
        var pp = PC.Profile(legacy);

        Assert.Equal(legacy, a.Personality);
        Assert.Equal(pp.Receptiveness, a.Receptiveness);
        Assert.Equal(pp.Diligence, a.Diligence);
        Assert.Equal(pp.Selflessness, a.Selflessness);
        Assert.Equal(now.Leadership, a.Leadership); // 統率は触らない
        Assert.Equal((a.Receptiveness, a.Diligence, a.Selflessness, a.SelfGrowthJitter),
            (b.Receptiveness, b.Diligence, b.Selflessness, b.SelfGrowthJitter));
        // 旧 PersonalityFactor は「代表値の勤勉さ＋隠れた個人差」で丸ごと再現する（Q47）。
        Assert.Equal(factor, PC.SelfGrowthFactor(a.Diligence, a.SelfGrowthJitter), 12);
    }

    [Fact]
    public void LegacySave_NormalStaysNeutral()
    {
        var json = AsLegacy(DevelopingPlayerSaves.ToJson(new DevelopingPlayer { Name = "旧" }), Personality.Normal, 1.0);
        var p = DevelopingPlayerSaves.FromJson(json);
        Assert.Equal(Personality.Normal, p.Personality);
        Assert.Equal(0.0, p.SelfGrowthJitter);
        Assert.Equal(1.0, PC.SelfGrowthFactor(p.Diligence, p.SelfGrowthJitter), 12);
    }

    [Fact]
    public void Save_V27ToV29_WithoutJitter_ReadsZero()
    {
        // v27〜v29（軸はあるが個人差の鍵が無い）の部員・AI校の選手は個人差0で読む。
        var dpJson = DevelopingPlayerSaves.ToJson(PersonalityFixture.Developing(Personality.Genius));
        var o = new JsonObject();
        foreach (var k in dpJson.Keys) if (k != "growthJitter") o.Add(k, dpJson[k]);
        Assert.Equal(0.0, DevelopingPlayerSaves.FromJson(o).SelfGrowthJitter);

        var pJson = PlayerSaves.ToJson(new Player { Name = "旧", SelfGrowthJitter = 0.04 });
        var po = new JsonObject();
        foreach (var k in pJson.Keys) if (k != "growthJitter") po.Add(k, pJson[k]);
        Assert.Equal(0.0, PlayerSaves.PlayerFromJson(po).SelfGrowthJitter);
        Assert.Equal(0.04, PlayerSaves.PlayerFromJson(pJson).SelfGrowthJitter, 12);
    }

    [Fact]
    public void LegacySave_AiPlayer_MigratesToRepresentative()
    {
        var now = PlayerSaves.ToJson(new Player { Name = "旧", Leadership = 30, BuntSuccessBonus = -0.06, ChanceHitFactor = 1.10 });
        var o = new JsonObject();
        foreach (var k in now.Keys)
            if (k is not ("receptiveness" or "diligence" or "selflessness")) o.Add(k, now[k]);
        o.Add("personality", "Genius");
        var p = PlayerSaves.PlayerFromJson(o);
        Assert.Equal(Personality.Genius, p.Personality);
        Assert.Equal(PC.Profile(Personality.Genius).Selflessness, p.Selflessness);
        // 旧セーブに焼き込まれていた試合効果はそのまま（移行で試合の効きを動かさない）。
        Assert.Equal(-0.06, p.BuntSuccessBonus, 12);
        Assert.Equal(1.10, p.ChanceHitFactor, 12);
    }

    [Fact]
    public void NewSave_RoundTripsAxesAndReading()
    {
        var p = PersonalityFixture.Developing(Personality.Craftsman, selfGrowthJitter: -0.037);
        p.PersonalityReading = new PersonalityReading().With(PersonalityAxis.Diligence, true);
        var back = DevelopingPlayerSaves.FromJson(DevelopingPlayerSaves.ToJson(p));
        Assert.Equal((p.Receptiveness, p.Diligence, p.Selflessness), (back.Receptiveness, back.Diligence, back.Selflessness));
        Assert.Equal(-0.037, back.SelfGrowthJitter, 12);
        Assert.Equal(Personality.Craftsman, back.Personality);
        Assert.Equal(p.PersonalityReading, back.PersonalityReading);
    }

    // ===== 見極め: 軸ごとの開示（Q37） =====

    [Fact]
    public void Insight_RevealsOneAxisAtATime_AndNamesTheTypeWhenAllFourAreRead()
    {
        var c = new InsightCoefficients { AccuracyBase = 1.0, AccuracyMax = 1.0 };
        var p = PersonalityFixture.Developing(Personality.HotBlood);
        var roster = new List<DevelopingPlayer> { p };
        var rng = new Xoshiro256Random(5);
        Personality? revealed = null;
        var axesSeen = new HashSet<PersonalityAxis>();
        for (var w = 0; w < 20_000 && revealed is null; w++)
        {
            foreach (var n in InsightModel.Week(roster, talentEye: 95, rng, c))
            {
                if (n.Topic != InsightTopic.Personality) continue;
                axesSeen.Add(n.PersonalityAxis);
                Assert.Equal(Personalities.AxisSideName(n.PersonalityAxis,
                    Personalities.CellHigh(Personality.HotBlood, n.PersonalityAxis)), n.Domain);
                if (axesSeen.Count < 4) Assert.Null(n.RevealedType);
                revealed = n.RevealedType;
            }
        }
        Assert.Equal(Personality.HotBlood, revealed);
        Assert.Equal(Personality.HotBlood, p.PersonalityReading.KnownType);
    }

    [Fact]
    public void Insight_Misread_ShowsTheOppositeSide()
    {
        var c = new InsightCoefficients { AccuracyBase = 0.0, AccuracyPerTalentEye = 0.0, NominatedAccuracyAdd = 0.0 };
        var p = PersonalityFixture.Developing(Personality.Hardworker);
        var rng = new Xoshiro256Random(11);
        for (var w = 0; w < 20_000 && !p.PersonalityReading.IsComplete; w++)
            InsightModel.Week(new[] { p }, talentEye: 95, rng, c);
        Assert.True(p.PersonalityReading.IsComplete);
        // 全軸を逆に読む＝↑統率↓素直↓勤勉・目立ちたがり＝暴れん坊に見える。
        Assert.Equal(Personality.Rowdy, p.PersonalityReading.KnownType);
    }
}

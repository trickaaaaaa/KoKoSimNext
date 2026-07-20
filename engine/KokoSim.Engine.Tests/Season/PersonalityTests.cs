using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 性格システム（設計書01 §1.1, CHANGELOG 22b）。4傾向値＝8タイプ。
/// 生成（タイプ先決め・決定論・分布）／効果（②素直さ・③勤勉さ・④姿勢）／平均中立（帯不変）／
/// Insightの「？→判明」を検証する。各軸の効き先は1つ・二重計上しない。
/// </summary>
public sealed class PersonalityTests
{
    private static readonly RosterCoefficients Roster = new();
    private static readonly PersonalityCoefficients PC = new();

    private static List<DevelopingPlayer> Cohort(int seeds = 400)
    {
        var all = new List<DevelopingPlayer>();
        for (var s = 0; s < seeds; s++)
            all.AddRange(ProspectGenerator.Intake(1, Roster, new Xoshiro256Random((ulong)(9000 + s))));
        return all;
    }

    // ===== 生成: 決定論・分布 =====

    [Fact]
    public void Intake_Personality_IsDeterministic()
    {
        var a = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random(31));
        var b = ProspectGenerator.Intake(1, Roster, new Xoshiro256Random(31));
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Personality, b[i].Personality);
            Assert.Equal(a[i].PersonalityFactor, b[i].PersonalityFactor, 12);
            Assert.Equal(a[i].Leadership, b[i].Leadership);
        }
    }

    [Fact]
    public void Intake_AllEightTypesAppear_NeverNormal()
    {
        var cohort = Cohort();
        var seen = cohort.Select(p => p.Personality).ToHashSet();
        foreach (var t in Personalities.Spawnable)
            Assert.Contains(t, seen);
        Assert.DoesNotContain(Personality.Normal, seen);
    }

    [Fact]
    public void Intake_SpawnDistribution_TracksWeights()
    {
        var cohort = Cohort();
        var counts = cohort.GroupBy(p => p.Personality).ToDictionary(g => g.Key, g => g.Count());
        double Freq(Personality t) => counts.TryGetValue(t, out var n) ? (double)n / cohort.Count : 0.0;

        // 重み比が概ね頻度比に出る（努力家/マイペースは天才肌より明確に多い）。
        Assert.True(Freq(Personality.Hardworker) > Freq(Personality.Genius) * 2.0);
        Assert.True(Freq(Personality.MyPace) > Freq(Personality.Genius) * 2.0);
        // 天才肌は稀（5%未満）だが 0 ではない。
        Assert.InRange(Freq(Personality.Genius), 0.01, 0.10);
    }

    // ===== 平均中立（不変条件#5: 帯ドリフト最小化） =====

    [Fact]
    public void DefaultTable_IsApproximatelyMeanNeutral()
    {
        var profiles = PC.Profiles.Where(p => p.SpawnWeight > 0).ToList();
        var total = profiles.Sum(p => p.SpawnWeight);
        double W(System.Func<PersonalityProfile, double> f) => profiles.Sum(p => p.SpawnWeight * f(p)) / total;

        Assert.Equal(1.0, W(p => p.SelfGrowthFactor), 1);       // ③ 平均 ≈ 1.0（±0.05）
        Assert.Equal(1.0, W(p => p.CoachingReceptivity), 1);   // ② 平均 ≈ 1.0
        Assert.Equal(1.0, W(p => p.ChanceHitFactor), 1);       // ④ 長打質 平均 ≈ 1.0
        Assert.InRange(W(p => p.BuntSuccessBonus), -0.02, 0.02); // ④ 犠打補正 平均 ≈ 0
        Assert.InRange(W(p => p.LeadershipMeanOffset), -2.0, 2.0); // ① 統率偏り 平均 ≈ 0
    }

    [Fact]
    public void PersonalityFactor_PopulationMean_StaysNearOne()
    {
        var cohort = Cohort();
        var mean = cohort.Average(p => p.PersonalityFactor);
        // 従来（中心1.0）から大きくズレない＝育成到達帯を大きく動かさない。
        Assert.InRange(mean, 0.98, 1.03);
    }

    // ===== ②素直さ: 個別指導の効き =====

    [Fact]
    public void Coaching_ObedientBeatsStubborn_NullIsNeutral()
    {
        double Gain(Personality type, PersonalityCoefficients? pc)
        {
            var c = new TrainingCoefficients();
            var stages = new GrowthStageTable();
            var p = new DevelopingPlayer { Personality = type };
            p.SetLevel(AbilityKind.Contact, 30);
            p.SetCap(AbilityKind.Contact, 99);
            var before = p.Level(AbilityKind.Contact) + p.Exp(AbilityKind.Contact) / 100.0;
            for (var w = 0; w < 30; w++)
                DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 1, 1.0, stages, c,
                    personalities: pc);
            return (p.Level(AbilityKind.Contact) + p.Exp(AbilityKind.Contact) / 100.0) - before;
        }

        var obedient = Gain(Personality.HonorStudent, PC); // 素直（受容1.10）
        var stubborn = Gain(Personality.Genius, PC);       // 頑固（受容0.88）
        var neutralNull = Gain(Personality.HonorStudent, null); // 係数なし＝従来挙動

        Assert.True(obedient > stubborn, $"obedient={obedient} stubborn={stubborn}");
        // personalities=null は Normal相当（受容1.0）で従来と一致：素直な選手でも補正されない。
        var normal = Gain(Personality.Normal, PC);
        Assert.Equal(normal, neutralNull, 6);
    }

    // ===== ③勤勉さ: 生成された PersonalityFactor に効く（放置成長の代理） =====

    [Fact]
    public void SelfGrowth_DiligentTypesFactorHigherThanLazy()
    {
        // タイプ中心が factor に出る（ジッタσ=0.06 より差が大きい代表ペア）。
        Assert.True(PC.Profile(Personality.Hardworker).SelfGrowthFactor
                    > PC.Profile(Personality.MoodMaker).SelfGrowthFactor);
    }

    // ===== ④自己犠牲⇔目立ちたがり =====

    [Fact]
    public void Bunt_SelflessRaises_SpotlightLowers()
    {
        var c = new BaserunningCoefficients();
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 140 };
        Player Batter(double bonus) => new() { Bunt = 50, BuntSuccessBonus = bonus };

        var selfless = BuntResolver.SuccessRate(Batter(+0.05), pitcher, c);
        var neutral = BuntResolver.SuccessRate(Batter(0.0), pitcher, c);
        var spotlight = BuntResolver.SuccessRate(Batter(-0.06), pitcher, c);

        Assert.True(selfless > neutral);
        Assert.True(spotlight < neutral);
        Assert.Equal(0.05, selfless - neutral, 6);
    }

    [Fact]
    public void Projection_BakesChanceHitAndBuntScalars()
    {
        var dp = new DevelopingPlayer { Personality = Personality.MoodMaker };
        var player = RosterTeamBuilder.ToPlayer(dp, FieldPosition.LeftField, asPitcher: false,
            personalityCoeff: PC);
        var prof = PC.Profile(Personality.MoodMaker);
        Assert.Equal(Personality.MoodMaker, player.Personality);
        Assert.Equal(prof.ChanceHitFactor, player.ChanceHitFactor, 9);
        Assert.Equal(prof.BuntSuccessBonus, player.BuntSuccessBonus, 9);
        Assert.True(player.ChanceHitFactor > 1.0); // 目立ちたがりは得点圏強攻で上振れ
    }

    [Fact]
    public void Projection_NullPersonality_IsNeutral()
    {
        var dp = new DevelopingPlayer(); // Personality=Normal 既定
        var player = RosterTeamBuilder.ToPlayer(dp, FieldPosition.CenterField, asPitcher: false);
        Assert.Equal(Personality.Normal, player.Personality);
        Assert.Equal(0.0, player.BuntSuccessBonus, 9);
        Assert.Equal(1.0, player.ChanceHitFactor, 9);
    }

    // ===== Insight「？→判明」 =====

    [Fact]
    public void Insight_SurfacesPersonalityTopic()
    {
        var c = new InsightCoefficients();
        var roster = new List<DevelopingPlayer>
        {
            new() { Personality = Personality.HotBlood },
            new() { Personality = Personality.Genius },
            new() { Personality = Personality.Introvert },
        };
        var rng = new Xoshiro256Random(5);
        var sawPersonality = false;
        // 高い育成眼で頻度を上げ、多週回して性格トピックの発生を確認。
        for (var w = 0; w < 4000 && !sawPersonality; w++)
        {
            foreach (var n in InsightModel.Week(roster, talentEye: 95, rng, c))
                if (n.Topic == InsightTopic.Personality) sawPersonality = true;
        }
        Assert.True(sawPersonality);
    }
}

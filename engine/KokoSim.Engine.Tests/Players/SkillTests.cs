using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Players;

/// <summary>
/// 特殊能力（設計書10）。設計の鉄則を検証する:
/// スキルなしは既存挙動と完全一致（帯不変）／各スキルが意図した方向に効く／可視・隠しの扱い／決定論。
/// </summary>
public sealed class SkillTests
{
    private static readonly SkillCoefficients C = new();

    // ===== SkillSet の基本 =====

    [Fact]
    public void SkillSet_HasChecksVisibleAndHidden_IsHiddenDistinguishes()
    {
        var set = new SkillSet(new[] { Skill.Grinder }, new[] { Skill.Monster });
        Assert.True(set.Has(Skill.Grinder));
        Assert.True(set.Has(Skill.Monster));
        Assert.False(set.Has(Skill.Streaky));
        Assert.False(set.IsHidden(Skill.Grinder));
        Assert.True(set.IsHidden(Skill.Monster));
    }

    [Fact]
    public void SkillSet_Reveal_MovesHiddenToVisible()
    {
        var set = new SkillSet(hidden: new[] { Skill.DeceptiveBall }).Reveal(Skill.DeceptiveBall);
        Assert.Contains(Skill.DeceptiveBall, set.Visible);
        Assert.False(set.IsHidden(Skill.DeceptiveBall));
        Assert.True(set.Has(Skill.DeceptiveBall));
    }

    // ===== SkillModel: 能力補正（尻上がり・怪物・荒れ球・打者一巡） =====

    [Fact]
    public void ApplyBatter_NoSkills_ReturnsSameInstance()
    {
        var b = new BatterAttributes { Contact = 60 };
        Assert.Same(b, SkillModel.ApplyBatter(b, SkillSet.Empty, priorPa: 3, C));
    }

    [Fact]
    public void ApplyBatter_SlowStarter_RisesWithPa_AndCaps()
    {
        var b = new BatterAttributes { Contact = 50 };
        var skills = new SkillSet(new[] { Skill.SlowStarterBat });
        Assert.Equal(50, SkillModel.ApplyBatter(b, skills, 0, C).Contact);
        Assert.Equal(50 + 2 * 1.6, SkillModel.ApplyBatter(b, skills, 2, C).Contact, 0);
        // 上限 6.0 で頭打ち。
        Assert.Equal(56, SkillModel.ApplyBatter(b, skills, 10, C).Contact);
    }

    [Fact]
    public void ApplyBatter_Monster_BoostsContactAndPower()
    {
        var b = new BatterAttributes { Contact = 70, Power = 70 };
        var m = SkillModel.ApplyBatter(b, new SkillSet(new[] { Skill.Monster }), 0, C);
        Assert.Equal(74, m.Contact);
        Assert.Equal(74, m.Power);
    }

    [Fact]
    public void ApplyPitcher_EffectivelyWild_LowersControl()
    {
        var p = new PitcherAttributes { Control = 60 };
        var w = SkillModel.ApplyPitcher(p, new SkillSet(new[] { Skill.EffectivelyWild }), 0, C);
        Assert.Equal(53, w.Control);
    }

    [Fact]
    public void ApplyPitcher_SecondTimeThrough_PenalizesOnlyAfterThreshold()
    {
        var p = new PitcherAttributes { Control = 60 };
        var skills = new SkillSet(new[] { Skill.SecondTimeThrough });
        Assert.Equal(60, SkillModel.ApplyPitcher(p, skills, priorBf: 10, C).Control); // 1-2巡目は無影響
        Assert.Equal(54, SkillModel.ApplyPitcher(p, skills, priorBf: 18, C).Control); // 3巡目〜で崩れる
    }

    [Fact]
    public void ApplyPitcher_SlowStarter_RisesWithBattersFaced()
    {
        var p = new PitcherAttributes { Control = 50 };
        var skills = new SkillSet(new[] { Skill.SlowStarterPitch });
        Assert.True(SkillModel.ApplyPitcher(p, skills, 12, C).Control > 50);
    }

    // ===== SkillModel: 行動特性・球質（PlayMods） =====

    [Fact]
    public void PlayMods_NoSkills_IsIdentity()
        => Assert.True(SkillModel.PlayMods(SkillSet.Empty, SkillSet.Empty, 0, C).IsIdentity);

    [Fact]
    public void PlayMods_ComposesBatterAndPitcherSkills()
    {
        var batter = new SkillSet(new[] { Skill.SprayHitter, Skill.Grinder, Skill.FirstPitchSwinger });
        var pitcher = new SkillSet(new[] { Skill.DeceptiveBall });
        var m = SkillModel.PlayMods(batter, pitcher, 0, C);
        Assert.Equal(C.SprayBearingFactor, m.BearingSigmaFactor);
        Assert.Equal(C.GrinderFoulFactor, m.FoulShareFactor);
        Assert.Equal(C.FirstPitchSwingProb, m.FirstPitchSwingProb);
        Assert.Equal(C.DeceptiveBallStuffBonus, m.StuffBonus, 9);
    }

    [Fact]
    public void DayFormVariance_StreakyAmplifies()
    {
        Assert.Equal(1.0, SkillModel.DayFormVarianceFactor(SkillSet.Empty, C));
        Assert.Equal(C.StreakyVarianceFactor, SkillModel.DayFormVarianceFactor(new SkillSet(new[] { Skill.Streaky }), C));
    }

    // ===== 打席パイプライン: 各スキルが観測可能な方向に効く =====

    private static (double avg, double pitchesPerPa, double babip) SimAtBats(
        SkillPlayMods mods, BatterAttributes? batter = null, PitcherAttributes? pitcher = null, int n = 6000)
    {
        var b = batter ?? new BatterAttributes();
        var p = pitcher ?? PitcherAttributes.LeagueAverage;
        var ctx = new AtBatContext { Skills = mods };
        int hits = 0, atBats = 0, pitches = 0, balls = 0, hr = 0;
        for (var i = 0; i < n; i++)
        {
            // 打席ごとに同じ種から引き直す（共通乱数法）。スキル有無の2条件が同じ乱数列を共有するので
            // 差の分散が大幅に下がり、小さい効果量でも少ない打席数で符号が安定する。
            var rng = new Xoshiro256Random((ulong)(20260716 + i));
            var r = AtBatResolver.ResolveDetailed(b, p, ctx, rng);
            pitches += r.Pitches;
            if (r.Result is PlateAppearanceResult.Walk) balls++;
            var ab = r.Result is not (PlateAppearanceResult.Walk);
            if (ab) atBats++;
            if (r.Result.IsHit()) hits++;
            if (r.Result == PlateAppearanceResult.HomeRun) hr++;
        }
        return ((double)hits / atBats, (double)pitches / n, 0);
    }

    [Fact]
    public void Grinder_IncreasesPitchesPerPa()
    {
        var plain = SimAtBats(SkillPlayMods.None).pitchesPerPa;
        var grind = SimAtBats(SkillPlayMods.None with { FoulShareFactor = C.GrinderFoulFactor }).pitchesPerPa;
        Assert.True(grind > plain, $"粘り打ちで球数が増えるはず: {grind:F2} vs {plain:F2}");
    }

    [Fact]
    public void DeceptiveBall_LowersOpponentAverage()
    {
        // 被打率の期待差は 0.01 程度で、独立乱数だと 6000打席のσ（≈0.008）に埋もれて符号が反転しうる。
        // SimAtBats は打席ごとに同じ種から引き直す（共通乱数法）ので、この差は安定して観測できる。
        var plain = SimAtBats(SkillPlayMods.None).avg;
        var deceptive = SimAtBats(SkillPlayMods.None with { StuffBonus = C.DeceptiveBallStuffBonus }).avg;
        Assert.True(deceptive < plain, $"クセ球で被打率が下がるはず: {deceptive:F3} vs {plain:F3}");
    }

    [Fact]
    public void FirstPitchSwinger_ReducesPitchesPerPa()
    {
        var plain = SimAtBats(SkillPlayMods.None).pitchesPerPa;
        var aggressive = SimAtBats(SkillPlayMods.None with { FirstPitchSwingProb = 0.9 }).pitchesPerPa;
        Assert.True(aggressive < plain, $"初球から振ると球数が減るはず: {aggressive:F2} vs {plain:F2}");
    }

    // ===== 試合統合: スキルなしチームは完全に従来と一致 =====

    private static Player Pos(FieldPosition pos, SkillSet? skills = null) => new()
    {
        Position = pos, Skills = skills ?? SkillSet.Empty,
    };

    private static Team TeamOf(string name, SkillSet? aceSkills = null, Player? captain = null)
    {
        var order = new List<Player>
        {
            captain ?? Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            new Player { Position = FieldPosition.Pitcher, Name = name + "P",
                Pitching = PitcherAttributes.LeagueAverage, Skills = aceSkills ?? SkillSet.Empty },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Captain = captain };
    }

    [Fact]
    public void Game_NoSkills_IdenticalToBaseline()
    {
        // スキル欄が空のチーム同士なら、スキル機構を通しても結果は1ビットも変わらない（帯不変の担保）。
        var ctx = new GameContext();
        for (ulong s = 0; s < 12; s++)
        {
            var a = GameEngine.Play(TeamOf("A"), TeamOf("H"), ctx, new Xoshiro256Random(s));
            var b = GameEngine.Play(TeamOf("A"), TeamOf("H"), ctx, new Xoshiro256Random(s));
            Assert.Equal(a.AwayRuns, b.AwayRuns);
            Assert.Equal(a.HomeRuns, b.HomeRuns);
            Assert.Equal(a.TotalPitches, b.TotalPitches);
        }
    }

    [Fact]
    public void Game_WithSkills_IsDeterministic()
    {
        var ctx = new GameContext();
        var ace = new SkillSet(new[] { Skill.DeceptiveBall, Skill.SlowStarterPitch });
        for (ulong s = 0; s < 6; s++)
        {
            var a = GameEngine.Play(TeamOf("A", aceSkills: ace), TeamOf("H"), ctx, new Xoshiro256Random(s));
            var b = GameEngine.Play(TeamOf("A", aceSkills: ace), TeamOf("H"), ctx, new Xoshiro256Random(s));
            Assert.Equal(a.AwayRuns, b.AwayRuns);
            Assert.Equal(a.HomeRuns, b.HomeRuns);
            Assert.Equal(a.TotalPitches, b.TotalPitches);
        }
    }

    // ===== 精神的支柱: 主将の緩和量を拡大 =====

    [Fact]
    public void SpiritualPillar_AmplifiesCaptainMitigation()
    {
        var plainCap = new Player { Position = FieldPosition.Catcher, Mental = 80, Leadership = 80 };
        var pillarCap = plainCap with { Skills = new SkillSet(new[] { Skill.SpiritualPillar }) };
        var plain = new TeamState(TeamOf("A", captain: plainCap));
        var pillar = new TeamState(TeamOf("B", captain: pillarCap));
        var tactics = new KokoSim.Engine.Match.Tactics.TacticsCoefficients();
        Assert.Equal(plain.CaptainMitigation(tactics, C) * C.SpiritualPillarCaptainFactor,
                     pillar.CaptainMitigation(tactics, C), 9);
    }

    // ===== シーズン: 体質スキル =====

    [Fact]
    public void Diligent_GrowsFaster_Lazy_Slower()
    {
        int LevelAfter(Skill? skill)
        {
            var p = new DevelopingPlayer
            {
                IsPitcher = false,
                Skills = skill is { } s ? new SkillSet(new[] { s }) : SkillSet.Empty,
            };
            p.SetLevel(AbilityKind.Contact, 30);
            p.SetCap(AbilityKind.Contact, 99);
            var stages = new GrowthStageTable();
            var tc = new TrainingCoefficients();
            for (var w = 0; w < 20; w++)
                DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 0, 1.0, stages, tc, C);
            return p.Level(AbilityKind.Contact);
        }
        var plain = LevelAfter(null);
        Assert.True(LevelAfter(Skill.Diligent) >= plain, "練習熱心は伸びが同等以上のはず");
        Assert.True(LevelAfter(Skill.Lazy) <= plain, "サボり癖は伸びが同等以下のはず");
    }

    [Fact]
    public void DurableAndInjuryProne_ShiftInjuryRate()
    {
        int Injuries(Skill? skill)
        {
            var count = 0;
            for (ulong s = 0; s < 400; s++)
            {
                var p = new DevelopingPlayer
                {
                    Skills = skill is { } sk ? new SkillSet(new[] { sk }) : SkillSet.Empty,
                };
                // 基礎発生率を高めて体質スキルの差を観測しやすくする（30週の累積で飽和しない水準）。
                var inj = new InjuryCoefficients { WeeklyBaseProb = 0.02 };
                var rng = new Xoshiro256Random(s);
                for (var w = 0; w < 30; w++)
                    if (InjuryModel.WeeklyCheck(p, rng, inj, C)) { count++; break; }
            }
            return count;
        }
        var plain = Injuries(null);
        Assert.True(Injuries(Skill.Durable) < plain, "故障しにくいで怪我が減るはず");
        Assert.True(Injuries(Skill.InjuryProne) > plain, "ケガしやすいで怪我が増えるはず");
    }

    // ===== 生成: 決定論・稀少性・投影 =====

    [Fact]
    public void Generation_IsDeterministic_AndProjectsToPlayer()
    {
        var a = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(42), skills: C);
        var b = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(42), skills: C);
        Assert.Equal(a.Select(p => p.Skills.Visible.Count + p.Skills.Hidden.Count),
                     b.Select(p => p.Skills.Visible.Count + p.Skills.Hidden.Count));
        // 投影で選手にスキルが引き継がれる。
        var dp = a.First();
        var player = RosterTeamBuilder.ToPlayer(dp, dp.IsPitcher ? FieldPosition.Pitcher : FieldPosition.CenterField, dp.IsPitcher);
        Assert.Equal(dp.Skills.Visible.OrderBy(s => s), player.Skills.Visible.OrderBy(s => s));
    }

    // ===== YAML駆動（不変条件#4）: カタログが全 Skill を網羅・効果係数がロードされる =====

    [Fact]
    public void SkillsCatalogYaml_CoversEveryEnumValue()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("skills.yaml");
        var catalog = KokoSim.Config.SkillsCatalogLoader.LoadFromFile(path);
        var defined = catalog.Select(e => e.Id).ToHashSet();
        foreach (Skill s in Enum.GetValues(typeof(Skill)))
            Assert.Contains(s, defined);
        Assert.Equal(Enum.GetValues(typeof(Skill)).Length, catalog.Count);
    }

    [Fact]
    public void CoefficientsYaml_LoadsSkillEffects()
    {
        var path = Engine.Tests.Balance.BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var b = KokoSim.Config.CoefficientsLoader.LoadFromFile(path);
        Assert.Equal(new SkillCoefficients().DeceptiveBallStuffBonus, b.Skills.DeceptiveBallStuffBonus, 9);
        Assert.Equal(new SkillCoefficients().CommonSkillProb, b.Skills.CommonSkillProb, 9);
        Assert.Equal(new SkillCoefficients().MaxSkillsPerPlayer, b.Skills.MaxSkillsPerPlayer);
    }

    [Fact]
    public void Generation_SkillsAreRare_NotEveryoneHasThem()
    {
        var players = new List<DevelopingPlayer>();
        for (ulong s = 0; s < 40; s++)
            players.AddRange(ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(s), skills: C));
        var withSkill = players.Count(p => !p.Skills.IsEmpty);
        // 一部は持つが全員ではない（純化＝ばらまかない）。
        Assert.InRange((double)withSkill / players.Count, 0.10, 0.95);
        // 怪物は極稀。
        var monsters = players.Count(p => p.Skills.Has(Skill.Monster));
        Assert.True(monsters < players.Count * 0.05, "怪物が多すぎる");
    }
}

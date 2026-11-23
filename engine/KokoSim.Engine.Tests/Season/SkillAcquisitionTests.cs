using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 特殊能力の後天習得・克服と、隠しスキルが表に出る道（issue #1471・設計書10 §3）。
/// 道は4つ＝気づきで即開示／覚醒で有益スキル／公式戦の活躍で習得・克服／学年で体質の負スキルを克服。
/// </summary>
public sealed class SkillAcquisitionTests
{
    private static SkillAcquisitionCoefficients Always() => new()
    {
        AwakeningSkillProb = 1.0, GrandAwakeningSkillProb = 1.0, MatchSkillProb = 1.0,
        MatchOvercomeProb = 1.0, GradeOvercomeProb = 1.0,
    };

    private static SkillAcquisitionCoefficients Never() => new()
    {
        AwakeningSkillProb = 0, GrandAwakeningSkillProb = 0, MatchSkillProb = 0,
        MatchOvercomeProb = 0, GradeOvercomeProb = 0,
    };

    private static DevelopingPlayer Player(int id, SkillSet? skills = null, int grade = 2)
        => new() { Id = id, Grade = grade, Skills = skills ?? SkillSet.Empty };

    // ===== SkillSet =====

    [Fact]
    public void Acquire_RefusesOppositeSkill_AndRevealsHiddenCopy()
    {
        var s = new SkillSet(visible: new[] { Skill.ChokeHitter }, hidden: new[] { Skill.Grinder });
        Assert.Same(s, s.Acquire(Skill.ClutchHitter));                 // 反対（チャンス×）を持つ＝付けない
        var t = s.Acquire(Skill.Grinder);                               // 隠しで持っていたもの＝表へ
        Assert.Contains(Skill.Grinder, t.Visible);
        Assert.DoesNotContain(Skill.Grinder, t.Hidden);
        Assert.Contains(Skill.Lazy, SkillSet.Empty.Acquire(Skill.Lazy).Visible);
    }

    [Fact]
    public void Remove_DropsFromBothSides()
    {
        var s = new SkillSet(visible: new[] { Skill.Lazy }, hidden: new[] { Skill.InjuryProne });
        Assert.False(s.Remove(Skill.Lazy).Has(Skill.Lazy));
        Assert.False(s.Remove(Skill.InjuryProne).Has(Skill.InjuryProne));
        Assert.Same(s, s.Remove(Skill.Durable));
    }

    // ===== 気づきで即開示 =====

    [Fact]
    public void Insight_RevealsHiddenSkill_OnlyWhenAccurate()
    {
        var c = new InsightCoefficients
        {
            BaseWeeklyProb = 1.0, ProbPerTalentEye = 0, AccuracyBase = 1.0, AccuracyPerTalentEye = 0, AccuracyMax = 1.0,
            HiddenSkillTopicProb = 1.0,
        };
        var p = Player(1, new SkillSet(hidden: new[] { Skill.DeceptiveBall }));
        var n = Assert.Single(InsightModel.Week(new[] { p }, 50, new Xoshiro256Random(1), c));
        Assert.Equal(InsightTopic.HiddenSkill, n.Topic);
        Assert.Equal(Skill.DeceptiveBall, n.HiddenSkill);
        Assert.Contains(Skill.DeceptiveBall, p.Skills.Visible);
        Assert.Empty(p.Skills.Hidden);

        // 読み違え（精度0）のときは開示しない＝隠しのまま
        var q = Player(2, new SkillSet(hidden: new[] { Skill.DeceptiveBall }));
        var wrong = c with { AccuracyBase = 0.0, AccuracyMax = 0.0 };
        var m = Assert.Single(InsightModel.Week(new[] { q }, 50, new Xoshiro256Random(1), wrong));
        Assert.NotEqual(InsightTopic.HiddenSkill, m.Topic);
        Assert.True(q.Skills.IsHidden(Skill.DeceptiveBall));
    }

    [Fact]
    public void Insight_WithoutHiddenSkills_DrawsExactlyAsBefore()
    {
        // 隠しスキルを持たない選手の抽選列は開示の導入前と同じ（確率0と1で同じ結果）。
        var c0 = new InsightCoefficients { BaseWeeklyProb = 1.0, HiddenSkillTopicProb = 0.0 };
        var c1 = c0 with { HiddenSkillTopicProb = 1.0 };
        var a = InsightModel.Week(new[] { Player(1, new SkillSet(visible: new[] { Skill.Grinder })) }, 50, new Xoshiro256Random(9), c0);
        var b = InsightModel.Week(new[] { Player(1, new SkillSet(visible: new[] { Skill.Grinder })) }, 50, new Xoshiro256Random(9), c1);
        Assert.Equal(a.Select(x => (x.Topic, x.Domain)), b.Select(x => (x.Topic, x.Domain)));
    }

    // ===== 覚醒で有益スキル =====

    [Fact]
    public void Awakening_GivesBeneficialSkillOfThatDomain()
    {
        var bat = Player(1);
        var pit = Player(2);
        var def = Player(3);
        var growth = new[]
        {
            new GrowthNotice(bat, GrowthEventType.Awakening, null) { Domain = GrowthDomain.Batting },
            new GrowthNotice(pit, GrowthEventType.GrandAwakening, null) { Domain = GrowthDomain.Pitching },
            new GrowthNotice(def, GrowthEventType.Awakening, null) { Domain = GrowthDomain.Defense },
            new GrowthNotice(def, GrowthEventType.Slump, null),
        };
        var notices = SkillAcquisitionModel.OnGrowthEvents(growth, new Xoshiro256Random(3), Always());

        Assert.Equal(2, notices.Count);
        var b = notices.Single(n => n.Player == bat);
        Assert.Contains(b.Skill, SkillAcquisitionModel.BattingGain);
        Assert.Equal(SkillChangeKind.Acquired, b.Kind);
        Assert.Equal(SkillChangeCause.Awakening, b.Cause);
        Assert.Contains(b.Skill, bat.Skills.Visible);
        Assert.Contains(notices.Single(n => n.Player == pit).Skill, SkillAcquisitionModel.PitchingGain);
        Assert.True(def.Skills.IsEmpty);

        Assert.Empty(SkillAcquisitionModel.OnGrowthEvents(growth, new Xoshiro256Random(3), Never()));
    }

    [Fact]
    public void Awakening_ManifestsDormantSkillFirst()
    {
        var p = Player(1, new SkillSet(hidden: new[] { Skill.ClutchHitter, Skill.Lazy }));
        var growth = new[] { new GrowthNotice(p, GrowthEventType.Awakening, null) { Domain = GrowthDomain.Batting } };
        var n = Assert.Single(SkillAcquisitionModel.OnGrowthEvents(growth, new Xoshiro256Random(1), Always()));
        Assert.Equal(Skill.ClutchHitter, n.Skill);
        Assert.Equal(SkillChangeKind.Manifested, n.Kind);
        Assert.Contains(Skill.ClutchHitter, p.Skills.Visible);
        Assert.True(p.Skills.IsHidden(Skill.Lazy));   // 分野外の隠し（負）は出さない
    }

    [Fact]
    public void Acquisition_NeverGivesOppositeSkill_AndRespectsCap()
    {
        // 打撃の有益プールのうち、チャンス○だけ反対（チャンス×）で塞がれている。何度引いても付かない。
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var p = Player(1, new SkillSet(visible: new[] { Skill.ChokeHitter }));
            var growth = new[] { new GrowthNotice(p, GrowthEventType.Awakening, null) { Domain = GrowthDomain.Batting } };
            SkillAcquisitionModel.OnGrowthEvents(growth, new Xoshiro256Random(seed), Always());
            Assert.False(p.Skills.Has(Skill.ClutchHitter));
        }

        var full = Player(2, new SkillSet(visible: new[] { Skill.Moodmaker, Skill.Durable, Skill.Diligent, Skill.RoleModel }));
        var g = new[] { new GrowthNotice(full, GrowthEventType.Awakening, null) { Domain = GrowthDomain.Batting } };
        Assert.Empty(SkillAcquisitionModel.OnGrowthEvents(g, new Xoshiro256Random(1), Always()));
    }

    // ===== 実戦経験 =====

    private static GameResult Game(BattingLine[] batting, PitchingLine[] pitching) => new()
    {
        AwayName = "相手", HomeName = "自校", AwayRuns = 1, HomeRuns = 5,
        InningsPlayed = 9, TotalPitches = 250, PitcherChanges = pitching.Length - 1,
        HomeBatting = batting, HomePitching = pitching,
    };

    [Fact]
    public void OfficialMatch_StandoutsGainOrOvercome_QuietOnesDoNot()
    {
        var hero = Player(1);
        var quiet = Player(2);
        var ace = Player(3, new SkillSet(visible: new[] { Skill.ShakyStart }));
        var closer = Player(4);
        var game = Game(
            new[]
            {
                new BattingLine(1, FieldPosition.CenterField, "英雄", 5, 5, 3, 1, 0, 0, 2, 0, 0, SourceId: 1),
                new BattingLine(2, FieldPosition.LeftField, "静か", 4, 4, 1, 0, 0, 0, 0, 0, 1, SourceId: 2),
            },
            new[]
            {
                new PitchingLine("エース", 21, 26, 4, 1, 8, 1, 100, SourceId: 3),
                new PitchingLine("抑え", 6, 7, 1, 0, 3, 0, 25, SourceId: 4),
            });

        var notices = SkillAcquisitionModel.AfterOfficialMatch(game, false, new[] { hero, quiet, ace, closer },
            new Xoshiro256Random(5), Always());

        Assert.Equal(3, notices.Count);
        Assert.DoesNotContain(notices, n => n.Player == quiet);
        var a = notices.Single(n => n.Player == ace);
        Assert.Equal((Skill.ShakyStart, SkillChangeKind.Overcome, SkillChangeCause.Match), (a.Skill, a.Kind, a.Cause));
        Assert.False(ace.Skills.Has(Skill.ShakyStart));
        Assert.Contains(notices.Single(n => n.Player == closer).Skill, SkillAcquisitionModel.ReliefGain);
        Assert.Contains(notices.Single(n => n.Player == hero).Skill, SkillAcquisitionModel.BattingGain);

        Assert.Empty(SkillAcquisitionModel.AfterOfficialMatch(game, false, new[] { Player(1), Player(3) },
            new Xoshiro256Random(5), Never()));
    }

    [Fact]
    public void OfficialMatch_ReadsOwnSide()
    {
        var hero = Player(1);
        var game = Game(System.Array.Empty<BattingLine>(), new[] { new PitchingLine("相手", 27, 30, 2, 0, 12, 0, 110, SourceId: 1) });
        // 自校が先攻（away）なら home 側の行は相手のもの＝何も起きない
        Assert.Empty(SkillAcquisitionModel.AfterOfficialMatch(game, managerIsAway: true, new[] { hero },
            new Xoshiro256Random(1), Always()));
    }

    // ===== 学年で克服 =====

    [Fact]
    public void YearAdvanced_OvercomesVisibleConstitutionFlaws_ForUpperGradesOnly()
    {
        var second = Player(1, new SkillSet(visible: new[] { Skill.Lazy, Skill.InjuryProne }), grade: 2);
        var freshman = Player(2, new SkillSet(visible: new[] { Skill.Lazy }), grade: 1);
        var hiddenFlaw = Player(3, new SkillSet(hidden: new[] { Skill.Lazy }), grade: 3);

        var notices = SkillAcquisitionModel.OnYearAdvanced(new[] { second, freshman, hiddenFlaw },
            new Xoshiro256Random(1), Always());

        Assert.Equal(2, notices.Count);
        Assert.All(notices, n => Assert.Equal((second, SkillChangeKind.Overcome, SkillChangeCause.Grade), (n.Player, n.Kind, n.Cause)));
        Assert.True(second.Skills.IsEmpty);
        Assert.True(freshman.Skills.Has(Skill.Lazy));
        Assert.True(hiddenFlaw.Skills.IsHidden(Skill.Lazy));
    }

    // ===== シーズンへの配線 =====

    [Fact]
    public void SeasonEngine_RunsAwakeningSkillsAndGradeOvercome_Deterministically()
    {
        static (int, string) Run()
        {
            var changes = new System.Collections.Generic.List<SkillChangeNotice>();
            var ctx = new SeasonContext
            {
                SkillAcquisition = new SkillAcquisitionCoefficients { AwakeningSkillProb = 1.0, GradeOvercomeProb = 1.0 },
                SkillObserver = changes.Add,
            };
            SeasonEngine.Run(3, ctx, new Xoshiro256Random(77));
            return (changes.Count, string.Join(",", changes.Select(c => c.Skill + ":" + c.Kind)));
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a, b);
        Assert.True(a.Item1 > 0);
    }
}

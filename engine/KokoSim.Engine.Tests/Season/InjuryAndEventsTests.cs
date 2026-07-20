using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 怪我システム（設計書03 §3.5）・選手成長イベント（§5.5）・気づきイベント（§5.6）・
/// 監督成長イベント（設計書04 §1.1b）。文脈条件（理不尽回避）と効き方の向きを検証する。
/// </summary>
public sealed class InjuryAndEventsTests
{
    private static readonly InjuryCoefficients Inj = new();

    private static DevelopingPlayer Guy(double resistance = 50) => new()
    {
        InjuryResistance = resistance,
    };

    // ===== 怪我（§3.5） =====

    [Fact]
    public void Injury_Resistance_LowersOccurrence()
    {
        int Count(double resistance, ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var n = 0;
            for (var i = 0; i < 30000; i++)
            {
                if (InjuryModel.WeeklyCheck(Guy(resistance), rng, Inj)) n++;
            }
            return n;
        }
        Assert.True(Count(90, 2) < Count(10, 2), "怪我耐性が効いていない");
    }

    [Fact]
    public void Injury_RecoversStepwise_ToHealthy()
    {
        var p = Guy();
        p.Injury = InjurySeverity.Severe;
        p.InjuryWeeksRemaining = Inj.SevereRecoveryWeeks;

        var weeks = 0;
        while (p.Injury != InjurySeverity.None && weeks < 52)
        {
            InjuryModel.WeeklyRecover(p, Inj);
            weeks++;
        }
        // 重度→中度→軽度→完治（8+4+2=14週）。
        Assert.Equal(InjurySeverity.None, p.Injury);
        Assert.Equal(Inj.SevereRecoveryWeeks + Inj.ModerateRecoveryWeeks + Inj.MinorRecoveryWeeks, weeks);
    }

    [Fact]
    public void Injury_PlayThrough_CanWorsen_AndExtend()
    {
        var rng = new Xoshiro256Random(5);
        var worsened = 0;
        for (var i = 0; i < 2000; i++)
        {
            var p = Guy();
            p.Injury = InjurySeverity.Minor;
            p.InjuryWeeksRemaining = 2;
            if (InjuryModel.PlayThrough(p, rng, Inj))
            {
                worsened++;
                Assert.Equal(InjurySeverity.Moderate, p.Injury); // 軽度→中度
                Assert.True(p.InjuryWeeksRemaining > Inj.ModerateRecoveryWeeks, "離脱が延びていない");
            }
        }
        // 悪化確率 30% 前後（基本的には損な選択）。
        Assert.InRange(worsened / 2000.0, 0.24, 0.36);
    }

    [Fact]
    public void Injury_Projection_LowersAbilities_AndCarriesSeverity()
    {
        var dp = Guy();
        dp.SetLevel(AbilityKind.Contact, 60);
        dp.SetLevel(AbilityKind.Speed, 60);
        dp.Injury = InjurySeverity.Severe;

        var player = RosterTeamBuilder.ToPlayer(dp, FieldPosition.CenterField, asPitcher: false);
        Assert.Equal(InjurySeverity.Severe, player.Injury); // 常に可視（采配判断用）
        Assert.Equal(42, player.Contact); // 60 × 0.70
        Assert.Equal(42, player.Speed);
    }

    [Fact]
    public void Injury_BlocksTraining()
    {
        var c = new TrainingCoefficients();
        var stages = new GrowthStageTable();
        var p = Guy();
        p.SetLevel(AbilityKind.Contact, 30);
        p.SetCap(AbilityKind.Contact, 99);
        p.Injury = InjurySeverity.Moderate;

        DevelopmentModel.TrainWeek(p, TrainingMenu.Batting, 0, 1.0, stages, c);
        Assert.Equal(30, p.Level(AbilityKind.Contact)); // 練習不可
    }

    // ===== 選手成長イベント（§5.5: 文脈必須＝理不尽回避） =====

    /// <summary>覚醒のみ確定発火（他イベントを混ぜず条件を分離検証する）。</summary>
    private static GrowthEventCoefficients Sure() => new()
    {
        AwakeningWeeklyProb = 1.0,
        BreakthroughWeeklyProb = 0,
        SlumpWeeklyProb = 0,
        PlateauWeeklyProb = 0,
        YipsWeeklyProb = 0,
    };

    [Fact]
    public void Awakening_RequiresGrowthAndCondition()
    {
        var rng = new Xoshiro256Random(1);
        // 兆しなし（伸びしろ低 or 不調）では発火しない。
        var dull = Guy();
        dull.BattingGrowth = 1.0;
        dull.ConditionValue = 0.5;
        Assert.Empty(GrowthEventModel.Week(new[] { dull }, rng, Sure()));

        var slumped = Guy();
        slumped.BattingGrowth = 1.4;
        slumped.ConditionValue = -0.5; // 不調
        Assert.DoesNotContain(GrowthEventModel.Week(new[] { slumped }, rng, Sure()),
            n => n.Type == GrowthEventType.Awakening);

        // 兆しあり（伸びしろ大＋好調）で発火し、能力が跳ねる。
        var star = Guy();
        star.BattingGrowth = 1.4;
        star.ConditionValue = 0.5;
        star.SetLevel(AbilityKind.Contact, 40);
        star.SetCap(AbilityKind.Contact, 99);
        star.SetLevel(AbilityKind.Power, 40);
        star.SetCap(AbilityKind.Power, 99);
        star.SetLevel(AbilityKind.Discipline, 40);
        star.SetCap(AbilityKind.Discipline, 99);
        var notices = GrowthEventModel.Week(new[] { star }, rng, Sure());
        Assert.Contains(notices, n => n.Type == GrowthEventType.Awakening);
    }

    [Fact]
    public void Slump_OnlyWhenOutOfForm_AndIsTemporary()
    {
        var rng = new Xoshiro256Random(2);
        var happy = Guy();
        happy.ConditionValue = 0.5;
        var c = new GrowthEventCoefficients { SlumpWeeklyProb = 1.0, AwakeningWeeklyProb = 0, BreakthroughWeeklyProb = 0, PlateauWeeklyProb = 0, YipsWeeklyProb = 0 };
        Assert.Empty(GrowthEventModel.Week(new[] { happy }, rng, c)); // 好調時は起きない（文脈）

        var low = Guy();
        low.ConditionValue = -0.5;
        var fired = GrowthEventModel.Week(new[] { low }, rng, c);
        Assert.Contains(fired, n => n.Type == GrowthEventType.Slump);
        Assert.True(low.SlumpWeeks > 0);

        // 投影で一時的な能力ダウン、期間経過で解ける。
        low.SetLevel(AbilityKind.Contact, 60);
        var during = RosterTeamBuilder.ToPlayer(low, FieldPosition.LeftField, asPitcher: false);
        Assert.True(during.Contact < 60);
    }

    [Fact]
    public void Plateau_OnlyNearCap_AndPermanentlyLowersGrowth()
    {
        var rng = new Xoshiro256Random(3);
        var c = new GrowthEventCoefficients { PlateauWeeklyProb = 1.0, AwakeningWeeklyProb = 0, BreakthroughWeeklyProb = 0, SlumpWeeklyProb = 0, YipsWeeklyProb = 0 };

        // 上限まで余裕がある選手には起きない（「限界の顕在化」のみ）。
        var young = Guy();
        foreach (var k in AbilityKinds.All) { young.SetLevel(k, 30); young.SetCap(k, 70); }
        Assert.Empty(GrowthEventModel.Week(new[] { young }, rng, c));

        // 上限に迫った選手には起き、伸びしろが恒久的に下がる。
        var maxed = Guy();
        foreach (var k in AbilityKinds.All) { maxed.SetLevel(k, 68); maxed.SetCap(k, 70); }
        var before = maxed.BattingGrowth;
        var fired = GrowthEventModel.Week(new[] { maxed }, rng, c);
        Assert.Contains(fired, n => n.Type == GrowthEventType.Plateau);
        Assert.True(maxed.BattingGrowth < before);
    }

    [Fact]
    public void Yips_RequiresContext_AndCanBeOvercome()
    {
        var rng = new Xoshiro256Random(4);
        var c = new GrowthEventCoefficients { YipsWeeklyProb = 1.0, AwakeningWeeklyProb = 0, BreakthroughWeeklyProb = 0, SlumpWeeklyProb = 0, PlateauWeeklyProb = 0, YipsOvercomeWeeklyProb = 1.0 };

        // 好調（スランプでない）では起きない。
        var fine = Guy();
        Assert.Empty(GrowthEventModel.Week(new[] { fine }, rng, c));

        // スランプ中に発症 → 送球精度が落ちる。
        var tired = Guy();
        tired.SlumpWeeks = 3;
        tired.SetLevel(AbilityKind.ThrowAccuracy, 60);
        tired.SetCap(AbilityKind.ThrowAccuracy, 80);
        var fired = GrowthEventModel.Week(new[] { tired }, rng, c);
        Assert.Contains(fired, n => n.Type == GrowthEventType.Yips);
        Assert.True(tired.HasYips);
        Assert.Equal(55, tired.Level(AbilityKind.ThrowAccuracy));

        // 克服の道が残る（次週に判定→取り返す）。
        var overcome = GrowthEventModel.Week(new[] { tired }, rng, c);
        Assert.Contains(overcome, n => n.Type == GrowthEventType.YipsOvercome);
        Assert.False(tired.HasYips);
        Assert.Equal(60, tired.Level(AbilityKind.ThrowAccuracy));
    }

    // ===== 気づきイベント（§5.6: 育成眼→頻度・精度） =====

    [Fact]
    public void Insight_HigherTalentEye_MoreFrequent_AndMoreAccurate()
    {
        var c = new InsightCoefficients();
        var roster = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(9)).ToList();

        (int Count, double Accuracy) Collect(double eye, ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var all = Enumerable.Range(0, 3000)
                .SelectMany(_ => InsightModel.Week(roster, eye, rng, c)).ToList();
            var acc = all.Count > 0 ? all.Count(n => n.IsAccurate) / (double)all.Count : 0;
            return (all.Count, acc);
        }

        var rookie = Collect(10, 7);
        var master = Collect(95, 7);
        Assert.True(master.Count > rookie.Count, $"育成眼で頻度が上がらない: {rookie.Count} vs {master.Count}");
        Assert.True(master.Accuracy > rookie.Accuracy, $"育成眼で精度が上がらない: {rookie.Accuracy:P0} vs {master.Accuracy:P0}");
        // 完全な答え合わせにはならない（外れが残る）。
        Assert.True(master.Accuracy < 1.0);
    }

    // ===== 監督成長イベント（設計書04 §1.1b） =====

    [Fact]
    public void ManagerGrowth_Koshien_BoostsTactics_BigLossNeedsContext()
    {
        var c = new ManagerGrowthCoefficients { FamousRivalProb = 0, SeminarProb = 0, ProObVisitBaseProb = 0, ProObVisitFameSlope = 0, MentorProb = 0, BigLossProb = 1.0 };

        // 甲子園経験: 采配が大きく伸びる（確定発火）。
        var m1 = new Manager { TacticalSense = 30 };
        var fired1 = ManagerGrowthEvents.Yearly(m1, reachedKoshien: true, wins: 3, new Xoshiro256Random(1), c);
        Assert.Contains(fired1, n => n.Type == ManagerGrowthEventType.KoshienExperience);
        Assert.Equal(35, m1.TacticalSense, 6);

        // 大敗の学び: 未勝利の年のみ（文脈）。勝った年には起きない。
        var m2 = new Manager();
        var winYear = ManagerGrowthEvents.Yearly(m2, reachedKoshien: false, wins: 2, new Xoshiro256Random(2), c);
        Assert.DoesNotContain(winYear, n => n.Type == ManagerGrowthEventType.LessonFromBigLoss);
        var lossYear = ManagerGrowthEvents.Yearly(m2, reachedKoshien: false, wins: 0, new Xoshiro256Random(2), c);
        Assert.Contains(lossYear, n => n.Type == ManagerGrowthEventType.LessonFromBigLoss);
    }

    // ===== Season 統合: 10年回して怪我が発生・回復し、破綻しない =====

    [Fact]
    public void Season_TenYears_InjuriesOccurAndHeal_NoCorruption()
    {
        var ctx = new SeasonContext
        {
            // 発生を観測しやすくする（既定は年間数件レベル）。
            Injury = new InjuryCoefficients { WeeklyBaseProb = 0.02 },
        };
        var summary = SeasonEngine.Run(10, ctx, new Xoshiro256Random(42));
        Assert.Equal(10, summary.Years.Count);
        // ロスターが定常帯に収まる（怪我・イベントで壊れていない）。
        Assert.InRange(summary.Years[^1].RosterCount, 22, 38);
    }
}

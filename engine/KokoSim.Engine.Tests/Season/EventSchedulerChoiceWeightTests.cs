using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Season.Events;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 効き先⑤（issue #1601・オーナー決定2026-11-25）: チーム状態（結束）が悪いほど、選択肢つき選手間イベント
/// （kind: Choice）の抽選重みが増す。<see cref="EventKind.Random"/> の重みには掛からないこと、
/// 既定（倍率1.0）は従来と同じ公平な抽選であることを確かめる。
/// </summary>
public sealed class EventSchedulerChoiceWeightTests
{
    private static List<GameEvent> Events() => new()
    {
        new GameEvent { Id = "r", Kind = EventKind.Random, Weight = 1.0, CooldownWeeks = 0 },
        new GameEvent { Id = "c", Kind = EventKind.Choice, Weight = 1.0, CooldownWeeks = 0 },
    };

    private static EventCoefficients AlwaysFire => new() { WeeklyFireProbability = 1.0, MaxRandomPerWeek = 1 };

    private static (int randomCount, int choiceCount) Run(double choiceWeightMultiplier, int trials)
    {
        var scheduler = new EventScheduler(Events(), AlwaysFire) { ChoiceWeightMultiplier = choiceWeightMultiplier };
        var rng = new Xoshiro256Random(99);
        int randomCount = 0, choiceCount = 0;
        for (var week = 0; week < trials; week++)
        {
            foreach (var fired in scheduler.Week(week, rng))
            {
                if (fired.Event.Id == "r") randomCount++;
                else choiceCount++;
            }
        }
        return (randomCount, choiceCount);
    }

    [Fact]
    public void Multiplier_One_PicksBothEventsRoughlyEqually()
    {
        var (r, c) = Run(1.0, 4000);
        Assert.True(r > 0 && c > 0);
        // 等重みなら概ね半々（±10%程度の誤差は許容）。
        var ratio = (double)c / (r + c);
        Assert.InRange(ratio, 0.40, 0.60);
    }

    [Fact]
    public void HigherMultiplier_PicksChoiceEventMoreOften_ButNeverChangesRandomsShare()
    {
        var (rLow, cLow) = Run(1.0, 4000);
        var (rHigh, cHigh) = Run(2.0, 4000);

        var ratioLow = (double)cLow / (rLow + cLow);
        var ratioHigh = (double)cHigh / (rHigh + cHigh);
        // ×2 の重みなら Choice が選ばれる割合が増える（1/2 → 2/3 付近）。
        Assert.True(ratioHigh > ratioLow + 0.1, $"low={ratioLow} high={ratioHigh}");
        Assert.InRange(ratioHigh, 0.55, 0.78);
    }

    [Fact]
    public void Default_IsIdentity_OneBitMatchWithExplicitOne()
    {
        // 既定（プロパティ未設定）は 1.0 と同じ乱数消費・同じ発火列＝従来互換（不変条件#2）。
        var defaultScheduler = new EventScheduler(Events(), AlwaysFire);
        var explicitScheduler = new EventScheduler(Events(), AlwaysFire) { ChoiceWeightMultiplier = 1.0 };
        var rngA = new Xoshiro256Random(7);
        var rngB = new Xoshiro256Random(7);
        for (var week = 0; week < 50; week++)
        {
            var a = defaultScheduler.Week(week, rngA);
            var b = explicitScheduler.Week(week, rngB);
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++) Assert.Equal(a[i].Event.Id, b[i].Event.Id);
        }
    }
}

using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>
/// 練習計画→実効配分の解決（設計書03 §3.1）。プリセットは現在の budget を満たすよう再生成、
/// Custom は保存済み配分を budget にクランプ（超過は比例縮小、不足は idle）。乱数不使用の決定論。
/// </summary>
public static class TrainingPresets
{
    /// <summary>練習時間の最小刻み[分]。配分は必ずこの倍数になる（UIの±ステップと共有）。</summary>
    public const int StepMinutes = 10;

    // 各プリセットのメニュー群＋相対重み（末尾に休養1枠）。重みはメニュー配分の厚み（データ駆動化余地あり）。
    private static readonly (TrainingMenu Menu, double Weight)[] PitcherAuto =
    {
        (TrainingMenu.Pitching, 1), (TrainingMenu.BreakingBall, 1), (TrainingMenu.Running, 1),
        (TrainingMenu.VelocityTraining, 1), (TrainingMenu.Rest, 1),
    };

    private static readonly (TrainingMenu Menu, double Weight)[] BatterAuto =
    {
        (TrainingMenu.Batting, 1), (TrainingMenu.PowerHitting, 1), (TrainingMenu.PlateDiscipline, 1),
        (TrainingMenu.Strength, 1), (TrainingMenu.Defense, 1), (TrainingMenu.BaseRunning, 1),
        (TrainingMenu.Throwing, 1), (TrainingMenu.Bunt, 1), (TrainingMenu.Rest, 1),
    };

    private static readonly (TrainingMenu Menu, double Weight)[] BalancedPitcher =
    {
        (TrainingMenu.Pitching, 2), (TrainingMenu.VelocityTraining, 1), (TrainingMenu.BreakingBall, 1),
        (TrainingMenu.Running, 1), (TrainingMenu.Batting, 1), (TrainingMenu.Rest, 1),
    };

    private static readonly (TrainingMenu Menu, double Weight)[] BalancedBatter =
    {
        (TrainingMenu.Batting, 2), (TrainingMenu.Defense, 1), (TrainingMenu.BaseRunning, 1),
        (TrainingMenu.Strength, 1), (TrainingMenu.Throwing, 1), (TrainingMenu.Rest, 1),
    };

    private static readonly (TrainingMenu Menu, double Weight)[] DefenseFocus =
    {
        (TrainingMenu.Defense, 3), (TrainingMenu.Throwing, 2), (TrainingMenu.DefenseInfield, 2),
        (TrainingMenu.Batting, 1), (TrainingMenu.Rest, 1),
    };

    private static readonly (TrainingMenu Menu, double Weight)[] AceDevelopment =
    {
        (TrainingMenu.Pitching, 3), (TrainingMenu.VelocityTraining, 2), (TrainingMenu.BreakingBall, 2),
        (TrainingMenu.Running, 1), (TrainingMenu.Rest, 1),
    };

    private static readonly (TrainingMenu Menu, double Weight)[] SluggerDevelopment =
    {
        (TrainingMenu.PowerHitting, 3), (TrainingMenu.Batting, 2), (TrainingMenu.Strength, 2),
        (TrainingMenu.Defense, 1), (TrainingMenu.Rest, 1),
    };

    /// <summary>プランを実効配分（分）へ解決する。</summary>
    public static IReadOnlyList<MenuAllocation> Resolve(TrainingPlan plan, bool isPitcher, int budgetMinutes)
    {
        if (budgetMinutes <= 0) return System.Array.Empty<MenuAllocation>();

        if (plan.Preset == TrainingPreset.Custom)
            return ResolveCustom(plan.Allocations, budgetMinutes);

        var table = plan.Preset switch
        {
            TrainingPreset.PitcherAuto => PitcherAuto,
            TrainingPreset.BatterAuto => BatterAuto,
            TrainingPreset.Balanced => isPitcher ? BalancedPitcher : BalancedBatter,
            TrainingPreset.DefenseFocus => DefenseFocus,
            TrainingPreset.AceDevelopment => AceDevelopment,
            TrainingPreset.SluggerDevelopment => SluggerDevelopment,
            _ => isPitcher ? PitcherAuto : BatterAuto,
        };
        return DistributeByWeight(table, budgetMinutes);
    }

    /// <summary>手動配分を budget にクランプ。超過は比例縮小、不足は idle（そのまま）。</summary>
    private static IReadOnlyList<MenuAllocation> ResolveCustom(
        IReadOnlyList<MenuAllocation> allocations, int budgetMinutes)
    {
        var total = 0;
        foreach (var a in allocations) total += System.Math.Max(0, a.Minutes);
        if (total <= budgetMinutes) return allocations;

        // 超過: 各配分の分を重みとして budget を按分（比例縮小）。
        var weighted = new (TrainingMenu, double)[allocations.Count];
        for (var i = 0; i < allocations.Count; i++)
            weighted[i] = (allocations[i].Menu, System.Math.Max(0, allocations[i].Minutes));
        return DistributeByWeight(weighted, budgetMinutes);
    }

    /// <summary>重みに比例して budget を StepMinutes 刻みで配分（端数刻みは先頭から+1、決定論）。
    /// 配分はすべて StepMinutes の倍数になり、合計 = (budget / StepMinutes) × StepMinutes
    /// （budget が刻みの倍数なら合計=budget を厳密保証。端数未満は idle）。</summary>
    private static IReadOnlyList<MenuAllocation> DistributeByWeight(
        (TrainingMenu Menu, double Weight)[] table, int budgetMinutes)
    {
        var sumW = 0.0;
        foreach (var (_, w) in table) sumW += w;
        if (sumW <= 0) return System.Array.Empty<MenuAllocation>();

        var units = budgetMinutes / StepMinutes;   // 配分する刻み数（例: 300/10=30）
        if (units <= 0) return System.Array.Empty<MenuAllocation>();

        var unit = new int[table.Length];
        var assigned = 0;
        for (var i = 0; i < table.Length; i++)
        {
            unit[i] = (int)(units * table[i].Weight / sumW); // floor
            assigned += unit[i];
        }
        // 端数刻みを先頭から+1して刻み総数を units に一致させる（決定論）。
        var remainder = units - assigned;
        for (var i = 0; i < table.Length && remainder > 0; i++)
        {
            unit[i]++;
            remainder--;
        }

        var result = new List<MenuAllocation>(table.Length);
        for (var i = 0; i < table.Length; i++)
            if (unit[i] > 0) result.Add(new MenuAllocation(table[i].Menu, unit[i] * StepMinutes));
        return result;
    }
}

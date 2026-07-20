using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>1メニューへの練習時間配分[分]（設計書03 §3.1）。record struct で要素まで不変。</summary>
public readonly record struct MenuAllocation(TrainingMenu Menu, int Minutes);

/// <summary>お任せプリセット種別（設計書03 §3.1）。Custom=手動配分。</summary>
public enum TrainingPreset
{
    Custom,
    PitcherAuto,        // 投手お任せ
    BatterAuto,         // 野手お任せ
    Balanced,           // バランス型
    DefenseFocus,       // 守備重視型
    AceDevelopment,     // エース育成型
    SluggerDevelopment, // 主砲育成型
}

/// <summary>
/// 選手ごとの練習計画（不変・値型的, 設計書03 §3.1）。複数メニューへ練習時間[分]を配分する。
/// immutable なので「他選手からコピー」は target.Plan = source.Plan の代入だけでコピー元を汚さない。
/// </summary>
public sealed record TrainingPlan
{
    public TrainingPreset Preset { get; init; } = TrainingPreset.Custom;

    /// <summary>手動配分（Preset=Custom のとき採用）。プリセット時は無視され Resolve が再生成する。</summary>
    public IReadOnlyList<MenuAllocation> Allocations { get; init; } = Array.Empty<MenuAllocation>();

    /// <summary>配分分の合計[分]（UIの残り時間表示に使う）。</summary>
    public int TotalMinutes()
    {
        var sum = 0;
        foreach (var a in Allocations) sum += a.Minutes;
        return sum;
    }

    /// <summary>お任せ既定（投手/野手で自動選択）。</summary>
    public static TrainingPlan Auto(bool isPitcher) =>
        new() { Preset = isPitcher ? TrainingPreset.PitcherAuto : TrainingPreset.BatterAuto };
}

namespace KokoSim.Engine.Nation;

/// <summary>
/// 現代ルールのトグル群（設計書05 §1.3 / CHANGELOG 31）。DH・タイブレーク・球数制限。
/// 各ルールに「導入年」を持たせ、過去年代から始めるプレイでは導入年以降に自動適用。
/// 「元祖の時代の野球」向けに全トグルの手動OFFも可能（既定は年代に従う）。
/// </summary>
public sealed record ModernRules
{
    /// <summary>導入年（この年以降、既定でON）。実史準拠の既定値。</summary>
    public int DhIntroYear { get; init; } = 2025;         // 2025春センバツから
    public int TieBreakIntroYear { get; init; } = 2018;   // タイブレーク導入
    public int PitchLimitIntroYear { get; init; } = 2020; // 1週間500球

    /// <summary>手動OFF（true=そのルールを強制的に無効化。年代連動を上書き）。</summary>
    public bool DhForcedOff { get; init; }
    public bool TieBreakForcedOff { get; init; }
    public bool PitchLimitForcedOff { get; init; }

    /// <summary>指定年で有効か（導入年以降＝自動ON。ただし手動OFFが優先）。</summary>
    public bool DhEnabled(int year) => !DhForcedOff && year >= DhIntroYear;
    public bool TieBreakEnabled(int year) => !TieBreakForcedOff && year >= TieBreakIntroYear;
    public bool PitchLimitEnabled(int year) => !PitchLimitForcedOff && year >= PitchLimitIntroYear;

    /// <summary>1投手1週間の球数上限（設計書05 §1.3。PitchLimit無効時は上限なし=int.MaxValue）。</summary>
    public int WeeklyPitchLimit { get; init; } = 500;
    public int EffectiveWeeklyPitchLimit(int year)
        => PitchLimitEnabled(year) ? WeeklyPitchLimit : int.MaxValue;
}

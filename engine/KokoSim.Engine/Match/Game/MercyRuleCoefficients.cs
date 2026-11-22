namespace KokoSim.Engine.Match.Game;

/// <summary>
/// コールドゲーム（マーシールール）の成立条件（設計書05 §1.3, OPEN-QUESTIONS Q18）。
/// 「<see cref="EarlyInning"/> 回以降で <see cref="EarlyDiff"/> 点差以上」または
/// 「<see cref="LateInning"/> 回以降で <see cref="LateDiff"/> 点差以上」。
/// 有効かどうかは <see cref="GameContext.MercyRuleEnabled"/>（現代ルールトグル）が決める。
/// </summary>
public sealed record MercyRuleCoefficients
{
    public int EarlyInning { get; init; } = 5;
    public int EarlyDiff { get; init; } = 10;
    public int LateInning { get; init; } = 7;
    public int LateDiff { get; init; } = 7;
}

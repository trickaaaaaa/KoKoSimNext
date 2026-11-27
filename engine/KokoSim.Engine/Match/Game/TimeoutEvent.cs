namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 采配AIが送った伝令1件（issue #1739）。<see cref="IsTopHalf"/> は送った半イニング、<see cref="TeamIsAway"/> は送ったチームが先攻か、
/// <see cref="IsOffense"/> は攻撃伝令（打者へ）か守備伝令（マウンドへ）か。<see cref="PlateAppearance"/> はその打席の
/// <see cref="GameProgress.Log"/> 上の添字（0始まり＝何打席目の頭か）。<see cref="Messenger"/> は伝令役（null＝記録員が走る）。
/// 観測専用＝乱数を使わず、試合結果・決定論digest・統計帯に影響しない（<see cref="GameProgress.Timeouts"/>）。
/// </summary>
public sealed record TimeoutEvent(
    int Inning, bool IsTopHalf, bool TeamIsAway, bool IsOffense, int PlateAppearance, SubstitutionPlayer? Messenger);

using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 試合結果による ConditionValue フィードバック（設計書02 §3.3「試合での結果…で上下」, issue #46）。
/// <see cref="MatchGrowthModel"/> と同じ合流点（自校の詳細シム1試合ぶん）で、出場者の
/// ConditionValue を好投/好打で+、被弾/大敗で−へ動かす。乱数不使用＝決定論（不変条件#2）。
/// 効果量は週次の波（FormCoefficients.WeeklySigma=0.28）より一桁小さく、「調子が支配的にならない」
/// 原則（design-02 §3.3コメント）を踏襲する。
/// </summary>
public static class MatchConditionModel
{
    /// <summary>1試合ぶんを適用する。<paramref name="managerIsAway"/>=true なら detail の Away 側が自校。</summary>
    public static void Apply(
        GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster, FormCoefficients f)
    {
        var batting = managerIsAway ? detail.AwayBatting : detail.HomeBatting;
        var pitching = managerIsAway ? detail.AwayPitching : detail.HomePitching;
        var managerLost = managerIsAway ? detail.HomeWon : !detail.HomeWon && !detail.Tied;
        var blowoutLoss = managerLost && detail.RunDifferential >= f.MatchBlowoutLossMargin;

        var byId = new Dictionary<int, DevelopingPlayer>(roster.Count);
        foreach (var dp in roster) byId[dp.Id] = dp;

        foreach (var line in batting)
        {
            if (line.SourceId is not { } id || line.PlateAppearances <= 0) continue;
            if (!byId.TryGetValue(id, out var dp)) continue;
            ApplyDelta(dp, BattingDelta(line, f) + (blowoutLoss ? -f.MatchBlowoutLossPenalty : 0.0));
        }

        foreach (var line in pitching)
        {
            if (line.SourceId is not { } id || line.BattersFaced <= 0) continue;
            if (!byId.TryGetValue(id, out var dp)) continue;
            ApplyDelta(dp, PitchingDelta(line, f) + (blowoutLoss ? -f.MatchBlowoutLossPenalty : 0.0));
        }
    }

    /// <summary>好打（安打・本塁打）で+、不振（規定打数以上で無安打）で−。</summary>
    private static double BattingDelta(BattingLine l, FormCoefficients f)
    {
        var delta = l.Hits * f.MatchHitBonus + l.HomeRuns * f.MatchHomeRunBonus;
        if (l.AtBats >= f.MatchHitlessMinAtBats && l.Hits == 0) delta -= f.MatchHitlessPenalty;
        return delta;
    }

    /// <summary>好投（規定投球回以上・少失点）で+、被弾（大量失点）で−。</summary>
    private static double PitchingDelta(PitchingLine l, FormCoefficients f)
    {
        var delta = 0.0;
        if (l.Outs >= f.MatchQualityStartMinOuts && l.Runs <= f.MatchQualityStartMaxRuns)
            delta += f.MatchQualityStartBonus;
        if (l.Runs >= f.MatchRockedRunsThreshold) delta -= f.MatchRockedPenalty;
        return delta;
    }

    private static void ApplyDelta(DevelopingPlayer dp, double delta)
    {
        if (delta == 0.0) return;
        dp.ConditionValue = MathUtil.Clamp(dp.ConditionValue + delta, -1.0, 1.0);
    }
}

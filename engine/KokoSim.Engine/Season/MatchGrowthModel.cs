using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Season;

/// <summary>
/// 実戦成長ループ（設計書02 §5.3a, Q8・2026-07-20）。
/// 精神力(Mental)・走塁判断(Baserunning)・捕手リード(Lead)は「公式戦/練習試合の出場でのみ成長」する。
/// 詳細試合の <see cref="GameResult"/> ボックススコア（SourceId=DevelopingPlayer.Id）と自校ロスターを
/// 突き合わせ、出場者へ経験値を付与する。乱数を一切使わない決定論処理（不変条件#2）で、
/// 必要exp曲線（100×1.05^v）・成長段階係数（GrowthType×時期）・隠しcapは練習成長（DevelopmentModel）と同じ流儀。
/// </summary>
public static class MatchGrowthModel
{
    /// <summary>
    /// 1試合ぶんの実戦成長を適用する。<paramref name="managerIsAway"/>=true なら detail の Away 側が自校。
    /// 出場の定義: 打撃ラインで打席1以上（守備専任交代は現ボックススコアに現れないため対象外）、
    /// または投手ラインで打者1人以上と対戦。捕手リードは「捕手として出場した」選手のみ伸びる。
    /// </summary>
    public static void Apply(
        GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster,
        int week, SeasonCalendar calendar, GrowthStageTable stages, TrainingCoefficients c)
    {
        var batting = managerIsAway ? detail.AwayBatting : detail.HomeBatting;
        var pitching = managerIsAway ? detail.AwayPitching : detail.HomePitching;

        var byId = new Dictionary<int, DevelopingPlayer>(roster.Count);
        foreach (var dp in roster) byId[dp.Id] = dp;

        var grown = new HashSet<int>(); // 打撃/投手ライン両方に載る選手（無DHの投手等）の二重付与防止

        foreach (var line in batting)
        {
            if (line.SourceId is not { } id || line.PlateAppearances <= 0) continue;
            if (!byId.TryGetValue(id, out var dp) || !grown.Add(id)) continue;
            var stage = stages.Coefficient(dp.GrowthType, calendar.StageIndex(dp.Grade, week));
            GrowMentalAndBaserunning(dp, stage, c);
            if (line.Position == FieldPosition.Catcher) GrowLead(dp, stage, c);
        }

        foreach (var line in pitching)
        {
            if (line.SourceId is not { } id || line.BattersFaced <= 0) continue;
            if (!byId.TryGetValue(id, out var dp) || !grown.Add(id)) continue;
            var stage = stages.Coefficient(dp.GrowthType, calendar.StageIndex(dp.Grade, week));
            GrowMentalAndBaserunning(dp, stage, c);
        }
    }

    private static void GrowMentalAndBaserunning(DevelopingPlayer dp, double stageCoef, TrainingCoefficients c)
    {
        dp.Mental = GrowScalar(dp.Mental, dp.MentalCap, exp => dp.MentalExp = exp, dp.MentalExp,
            c.MatchMentalExp * stageCoef, c);
        DevelopmentModel.ApplyExp(dp, AbilityKind.Baserunning, c.MatchBaserunningExp * stageCoef, c);
    }

    private static void GrowLead(DevelopingPlayer dp, double stageCoef, TrainingCoefficients c)
        => dp.Lead = GrowScalar(dp.Lead, dp.LeadCap, exp => dp.LeadExp = exp, dp.LeadExp,
            c.MatchLeadExp * stageCoef, c);

    /// <summary>
    /// exp加算→必要exp消費でレベルアップ→cap到達で余剰破棄（DevelopmentModel.ApplyExp のスカラー版）。
    /// 戻り値=更新後の値。exp の書き戻しは setter 経由。
    /// </summary>
    private static int GrowScalar(int value, int cap, Action<double> setExp, double exp, double delta,
        TrainingCoefficients c)
    {
        if (value >= cap) { setExp(0.0); return value; }
        exp += delta;
        while (value < cap)
        {
            var required = c.RequiredExp(value);
            if (exp < required) break;
            exp -= required;
            value++;
        }
        if (value >= cap) exp = 0.0; // 上限到達時は余剰expを捨てる（練習成長と同じ）
        setExp(exp);
        return value;
    }
}

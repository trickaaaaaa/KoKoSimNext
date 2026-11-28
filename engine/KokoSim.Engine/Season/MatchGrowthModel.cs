using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Season;

/// <summary>
/// 実戦成長ループ（設計書02 §5.3a, Q8・2026-07-20）。
/// 精神力(Mental)・走塁判断(Baserunning)・捕手リード(Lead)は「公式戦/練習試合の出場でのみ成長」する。
/// 詳細試合の <see cref="GameResult"/> ボックススコア（SourceId=DevelopingPlayer.Id）と自校ロスターを
/// 突き合わせ、出場者へ経験値を付与する。乱数を一切使わない決定論処理（不変条件#2）で、
/// 必要exp曲線（100×level_up_growth^v）・成長段階係数（GrowthType×時期）・隠しcapは練習成長（DevelopmentModel）と同じ流儀。
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
            var stage = StageWithCaptain(dp, stages, calendar, week, c);
            GrowMentalAndBaserunning(dp, stage, c);
            if (line.Position == FieldPosition.Catcher) GrowLead(dp, stage, c);
        }

        foreach (var line in pitching)
        {
            if (line.SourceId is not { } id || line.BattersFaced <= 0) continue;
            if (!byId.TryGetValue(id, out var dp) || !grown.Add(id)) continue;
            var stage = StageWithCaptain(dp, stages, calendar, week, c);
            GrowMentalAndBaserunning(dp, stage, c);
        }
    }

    /// <summary>成長段階係数。主将に選ばれた本人だけ <see cref="TrainingCoefficients.CaptainMatchGrowthFactor"/> を掛ける（設計書09 §8.1）。</summary>
    private static double StageWithCaptain(DevelopingPlayer dp, GrowthStageTable stages, SeasonCalendar calendar,
        int week, TrainingCoefficients c)
    {
        var stage = stages.Coefficient(dp.GrowthType, calendar.StageIndex(dp.Grade, week));
        return dp.IsCaptain ? stage * c.CaptainMatchGrowthFactor : stage;
    }

    /// <summary>
    /// 成功体験ボーナス（設計書02 §5.3b・issue #1487）。好打→ミート/パワー、好投→制球/キレ/スタミナ、
    /// 好守→守備/捕球/送球精度へ少量のexpを付与する。<paramref name="kind"/> が大会の格（null＝練習試合）で、
    /// 大舞台ほど倍率が大きい。成長段階係数・才能上限・必要exp曲線は練習成長と同じ（ApplyExp 経由）。乱数なし。
    /// </summary>
    public static void ApplySuccess(
        GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster, TournamentKind? kind,
        int week, SeasonCalendar calendar, GrowthStageTable stages, TrainingCoefficients c)
    {
        var batting = managerIsAway ? detail.AwayBatting : detail.HomeBatting;
        var pitching = managerIsAway ? detail.AwayPitching : detail.HomePitching;
        var fielding = managerIsAway ? detail.AwayFielding : detail.HomeFielding;
        var teamErrors = managerIsAway ? detail.AwayErrors : detail.HomeErrors;
        var stageMult = c.MatchSuccessStageMult(kind);

        var byId = new Dictionary<int, DevelopingPlayer>(roster.Count);
        foreach (var dp in roster) byId[dp.Id] = dp;
        var ownErrors = new Dictionary<int, int>();
        foreach (var f in fielding)
            if (f.SourceId is { } fid) ownErrors[fid] = ownErrors.GetValueOrDefault(fid) + f.Errors;

        double Scale(DevelopingPlayer dp) => SuccessScale(dp, stages, calendar, week, stageMult);

        foreach (var line in batting)
        {
            if (line.SourceId is not { } id || !byId.TryGetValue(id, out var dp)) continue;
            if (IsGoodBatting(line, c))
            {
                DevelopmentModel.ApplyExp(dp, AbilityKind.Contact, c.MatchSuccessBatExp * Scale(dp), c);
                DevelopmentModel.ApplyExp(dp, AbilityKind.Power, c.MatchSuccessBatExp * Scale(dp), c);
            }
            if (IsGoodFielding(line, ownErrors.GetValueOrDefault(id), teamErrors, c))
            {
                var exp = c.MatchSuccessFieldExp * Scale(dp);
                DevelopmentModel.ApplyExp(dp, AbilityKind.Fielding, exp, c);
                DevelopmentModel.ApplyExp(dp, AbilityKind.Catching, exp, c);
                DevelopmentModel.ApplyExp(dp, AbilityKind.ThrowAccuracy, exp, c);
            }
        }

        foreach (var line in pitching)
        {
            if (line.SourceId is not { } id || !byId.TryGetValue(id, out var dp)) continue;
            if (!IsGoodPitching(line, c)) continue;
            var exp = c.MatchSuccessPitchExp * Scale(dp);
            DevelopmentModel.ApplyExp(dp, AbilityKind.Control, exp, c);
            DevelopmentModel.ApplyExp(dp, AbilityKind.PitchRank, exp, c);
            DevelopmentModel.ApplyExp(dp, AbilityKind.Stamina, exp, c);
        }
    }

    /// <summary>成功体験ボーナス1回ぶんの成長段階係数×大会の格倍率。今日のMVPのご褒美（<see cref="MatchMvp"/>）も同じ値を使う。</summary>
    internal static double SuccessScale(DevelopingPlayer dp, GrowthStageTable stages, SeasonCalendar calendar, int week, double stageMult)
        => stages.Coefficient(dp.GrowthType, calendar.StageIndex(dp.Grade, week)) * stageMult;

    internal static bool IsGoodBatting(BattingLine l, TrainingCoefficients c)
        => l.Hits >= c.MatchSuccessMinHits || l.Rbi >= c.MatchSuccessMinRbi || l.HomeRuns >= 1;

    internal static bool IsGoodPitching(PitchingLine l, TrainingCoefficients c)
        => (l.Outs >= c.MatchSuccessMinOuts && l.Runs <= c.MatchSuccessMaxRuns)
            || l.StrikeOuts >= c.MatchSuccessMinStrikeOuts;

    /// <summary>守備に就いた野手（投手・DH除く）が無失策で、チームの失策も少ない試合。</summary>
    internal static bool IsGoodFielding(BattingLine l, int ownErrors, int teamErrors, TrainingCoefficients c)
    {
        if (ownErrors > 0 || teamErrors > c.MatchSuccessMaxTeamErrors) return false;
        foreach (var pos in l.Positions)
            if (pos != FieldPosition.DesignatedHitter && pos != FieldPosition.Pitcher) return true;
        return false;
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
    internal static int GrowScalar(int value, int cap, Action<double> setExp, double exp, double delta,
        TrainingCoefficients c)
    {
        if (value >= cap) { setExp(0.0); return value; }
        exp += delta;
        while (value < cap)
        {
            // 必要expは今の物差しのLvで引く（issue #1080）＝伸びの手応えは物差しによらず同じ。
            var required = c.RequiredExp(Players.AbilityScale.Phys(value));
            if (exp < required) break;
            exp -= required;
            value++;
        }
        if (value >= cap) exp = 0.0; // 上限到達時は余剰expを捨てる（練習成長と同じ）
        setExp(exp);
        return value;
    }
}

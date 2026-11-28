using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Balance;

/// <summary>
/// 入部時点の投手寄り率の実測（issue #1792）。判定の物差しを守備位置適性から実際の投手能力へ
/// 差し替えたあと、<see cref="RosterCoefficients.PitcherRoleGeta"/> を振って旧判定の実測（32.88%・
/// 400,000個体・<see cref="PitcherRoleResolver"/> のコメント参照）へ揃える校正専用の計測ツール。
/// 係数・既定帯は動かさない＝Balance CLI <c>pitcher-role-calibration</c> からだけ叩く分析専用パス。
///
/// <paramref name="talentCenter"/> を振ると、校の強さ別に率が動かないか（Issue #174 の決定＝
/// 投手供給率は校の強さに連動しない）も同じコマンドで追える（<see cref="ProspectGenerationTests"/> 参照）。
/// </summary>
public static class PitcherRoleCalibration
{
    public sealed record Result(int Players, int LeansPitcherCount, double Share, double MeanDiff, double MeanBaseline);

    private static readonly TeamStrengthCoefficients Strength = new();

    /// <summary>
    /// <paramref name="cohorts"/> 学年ぶんの新入生を生成し、生成直後（遊び無し＝初回判定）の
    /// 投手寄り率を測る。1学年＝1コホート（IntakeMean≈10人）なので <paramref name="cohorts"/>=40,000 で
    /// 旧判定の実測（400,000個体）に近い規模になる。乱数は毎コホート独立シード（決定論）。
    /// <paramref name="talentCenter"/>=null は <see cref="RosterCoefficients.TalentCenterDefault"/>（一般校）。
    /// </summary>
    public static Result Run(int cohorts, ulong seed, RosterCoefficients rc, double? talentCenter = null)
    {
        var all = new List<DevelopingPlayer>();
        for (var i = 0; i < cohorts; i++)
            all.AddRange(ProspectGenerator.Intake(1, rc, new Xoshiro256Random(seed + (ulong)i), talentCenter: talentCenter));

        var leans = 0;
        var diffSum = 0.0;
        var baselineSum = 0.0;
        foreach (var p in all)
        {
            if (PitcherRoleResolver.LeansPitcher(p, rc)) leans++;
            var pitcher = TeamStrengthProfile.PitcherComposite(p, Strength);
            var nonPitcher = TeamStrengthProfile.NonPitcherComposite(p, Strength);
            diffSum += pitcher - nonPitcher;
            baselineSum += (pitcher + nonPitcher) / 2.0;
        }
        return new Result(all.Count, leans, all.Count > 0 ? leans / (double)all.Count : 0.0,
            all.Count > 0 ? diffSum / all.Count : 0.0, all.Count > 0 ? baselineSum / all.Count : 0.0);
    }

    public static string Report(Result r, int cohorts, ulong seed, RosterCoefficients rc, double? talentCenter = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 入部時点の投手寄り率（issue #1792）");
        sb.AppendLine();
        sb.AppendLine($"- コホート数: {cohorts.ToString(CultureInfo.InvariantCulture)} / シード起点: {seed}"
            + (talentCenter is { } tc ? $" / talentCenter={tc.ToString(CultureInfo.InvariantCulture)}" : ""));
        sb.AppendLine($"- 生成人数: {r.Players.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- 投手寄り: {r.LeansPitcherCount.ToString(CultureInfo.InvariantCulture)}人"
            + $"（{r.Share:P2}）");
        sb.AppendLine($"- 投手総合−打撃守備総合の平均（補正前）: {r.MeanDiff:F2} / baseline平均: {r.MeanBaseline:F2}");
        sb.AppendLine($"- 旧判定（適性ベース）の実測: 32.88%（400,000個体, PitcherRoleResolver コメント）");
        sb.AppendLine();
        sb.AppendLine("## 使用係数（roster:）");
        sb.AppendLine($"- pitcher_role_geta = {rc.PitcherRoleGeta.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- pitcher_role_hysteresis_band = {rc.PitcherRoleHysteresisBand.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- pitcher_role_strength_slope = {rc.PitcherRoleStrengthSlope.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- pitcher_role_strength_reference = {rc.PitcherRoleStrengthReference.ToString(CultureInfo.InvariantCulture)}");
        return sb.ToString();
    }
}

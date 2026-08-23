using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using NationState = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Career;

/// <summary>年度末の転任判定で起きたこと（設計書04 §1.3）。</summary>
public enum TransferEventKind
{
    /// <summary>何も起きない（教員: 残留判定に成功／フリー: この年はオファーなし）。</summary>
    None,
    /// <summary>教員: 残留ならず、選択肢なしで強制転任（拒否できない）。</summary>
    ForcedTransfer,
    /// <summary>教員: フリー化資格を獲得。以降の本拠地を選ぶ（残留も選べる）。</summary>
    BecameFreeAgent,
    /// <summary>フリー: 他校からオファーが届いた（残留も選べる）。</summary>
    FreeAgentOffer,
}

/// <summary>転任先の候補1件（実在の校データから作る。設計書04 §1.3）。</summary>
public sealed record TransferCandidate(
    int SchoolId, string SchoolName, int PrefectureId, int? DistrictId, double Strength, double Fame);

/// <summary>年度末の転任判定の結果。</summary>
public sealed record TransferResult(
    TransferEventKind Kind, IReadOnlyList<TransferCandidate> Candidates, bool CanStay)
{
    public static readonly TransferResult Stayed =
        new(TransferEventKind.None, Array.Empty<TransferCandidate>(), CanStay: true);
}

/// <summary>
/// 監督の転任判定（設計書04 §1.3）。<see cref="CareerEngine.Run"/>（統計シム）と同じ判定式
/// （フリー化資格→残留ロジスティック）を使うが、赴任先は sim のような捏造値でなく<b>実在の
/// <see cref="School"/> から選ぶ</b>（実ゲームは4000校の Nation を持つため）。
///
/// <para>純関数・決定論（不変条件#2）。呼び出し側は本編の乱数列を乱さない独立 Fork ストリームを渡すこと
/// （<see cref="ManagerGrowthEvents.Yearly"/> の <c>milestoneRng</c> と同じ作法）。</para>
/// </summary>
public static class ManagerTransferModel
{
    public static TransferResult Evaluate(
        Manager manager, int currentPrefectureId, NationState nation, IRandomSource rng, CareerCoefficients c)
    {
        if (manager.Status == ManagerStatus.Teacher)
        {
            // フリー化資格（設計書04 §1.3）。
            if (manager.KoshienAppearances >= c.FreeKoshienThreshold || manager.Fame >= c.FreeFameThreshold)
            {
                var candidates = PickCandidates(
                    nation, currentPrefectureId, c.FreeChoiceSchoolStrength, c.FreeAgentOfferSpread,
                    c.FreeAgentOfferCount, rng);
                return new TransferResult(TransferEventKind.BecameFreeAgent, candidates, CanStay: true);
            }

            // 残留判定（信頼度が高いほど校が引き留める）。CareerEngine.Run と同じロジスティック式。
            var z = (manager.Trust - c.RetainTrustThreshold) / c.RetainTrustSpread;
            var retainChance = c.RetainProbability / (1.0 + Math.Exp(-z));
            if (rng.NextDouble() < retainChance) return TransferResult.Stayed;

            // 強制転任。選択肢は1件だけ（拒否できない＝設計書どおり）。
            var forced = PickCandidates(
                nation, currentPrefectureId, c.NewSchoolStrengthMean, c.NewSchoolStrengthSd, 1, rng);
            return new TransferResult(TransferEventKind.ForcedTransfer, forced, CanStay: false);
        }

        // フリー監督: 残留自由・他校オファーの選択制。オファーが届かない年は何も起きない。
        if (rng.NextDouble() < c.FreeAgentOfferChance)
        {
            var candidates = PickCandidates(
                nation, currentPrefectureId, c.FreeChoiceSchoolStrength, c.FreeAgentOfferSpread,
                c.FreeAgentOfferCount, rng);
            return new TransferResult(TransferEventKind.FreeAgentOffer, candidates, CanStay: true);
        }
        return TransferResult.Stayed;
    }

    /// <summary>転任に伴う監督メタの更新（信頼度リセットのみ。設計書04 §1.3「監督能力と名声は持ち越し」）。</summary>
    public static void ApplyTransfer(Manager m, CareerCoefficients c) => m.Trust = c.TrustReset;

    /// <summary>
    /// 実在校から<paramref name="count"/>件の候補を選ぶ。同一県は除外（他県へ渡り歩く手触り）。
    /// 強さは正規分布サンプルに最も近い校を拾う（乱数は<paramref name="rng"/>だけを消費・決定論）。
    /// </summary>
    private static IReadOnlyList<TransferCandidate> PickCandidates(
        NationState nation, int excludePrefectureId, double targetStrength, double spread, int count,
        IRandomSource rng)
    {
        var pool = new List<School>();
        foreach (var s in nation.Schools)
            if (s.PrefectureId != excludePrefectureId) pool.Add(s);
        if (pool.Count == 0) return Array.Empty<TransferCandidate>();

        var picked = new List<TransferCandidate>(count);
        var used = new HashSet<int>();
        for (var i = 0; i < count && picked.Count < pool.Count; i++)
        {
            var target = rng.NextGaussian(targetStrength, spread);
            School? best = null;
            var bestDiff = double.MaxValue;
            foreach (var s in pool)
            {
                if (used.Contains(s.Id)) continue;
                var diff = Math.Abs(s.Strength - target);
                if (diff < bestDiff) { bestDiff = diff; best = s; }
            }
            if (best == null) break;
            used.Add(best.Id);
            picked.Add(new TransferCandidate(best.Id, best.Name, best.PrefectureId, best.DistrictId, best.Strength, best.Fame));
        }
        return picked;
    }
}

using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 能動勧誘枠の係数（設計書04 §2.2・issue #588）。
/// <b>「補強の主役ではなくスパイス」</b>という位置づけを数字で守るための帯。
/// </summary>
public sealed record RecruitCoefficients
{
    /// <summary>中学生リストに載る人数。</summary>
    public int ListSize { get; init; } = 12;

    /// <summary>
    /// 中学生の才能中心値への上乗せ。<b>わざわざ名前が挙がる中学生</b>なので受動枠より上振れさせる
    /// （受動枠の中心32に対して+10＝リストの平均が「C以上が1軸ある」あたりに寄る）。
    /// </summary>
    public double TalentCenterAdd { get; init; } = 10.0;

    /// <summary>他県から載る割合（残りは自県）。地元だけだと寮を建てる意味が出ない。</summary>
    public double OutOfPrefectureRatio { get; init; } = 0.65;

    // --- 成功率（設計書04 §2.2「名声 × 距離（寮でカバー） × 競合校の格」） ---
    /// <summary>基礎成功率（名声0・同県・競合なし・訪問0のとき）。</summary>
    public double BaseProbability { get; init; } = 0.20;
    /// <summary>名声1あたりの上昇。名声100で +0.35。</summary>
    public double ProbPerFame { get; init; } = 0.0035;
    /// <summary>訪問1回あたりの上昇。</summary>
    public double ProbPerVisit { get; init; } = 0.09;
    /// <summary>訪問による上昇の上限（通い詰めれば確実、にはしない）。</summary>
    public double VisitGainMax { get; init; } = 0.36;
    /// <summary>競合校のティア1段あたりの低下（自校のティアとの差で効く）。</summary>
    public double ProbPerRivalTier { get; init; } = 0.08;
    /// <summary>成功率の下限・上限（0%や100%にはしない）。</summary>
    public double ProbabilityMin { get; init; } = 0.02;
    public double ProbabilityMax { get; init; } = 0.92;

    /// <summary>距離ごとの成功率の減点（同県／同地方／隣接地方／遠方）。</summary>
    public double PenaltySameRegion { get; init; } = 0.06;
    public double PenaltyNeighborRegion { get; init; } = 0.14;
    public double PenaltyFar { get; init; } = 0.24;

    /// <summary>寮が距離の減点を打ち消す割合（1.0で完全に打ち消す）。設計「距離（寮でカバー）」の実体。</summary>
    public double DormDistanceRelief { get; init; } = 0.6;

    // --- 訪問費用[万円]（部費から。設計書04 §1.4「勧誘の訪問費用が要るなら部費側」） ---
    public double VisitCostSamePrefecture { get; init; } = 5.0;
    public double VisitCostSameRegion { get; init; } = 10.0;
    public double VisitCostNeighborRegion { get; init; } = 20.0;
    public double VisitCostFar { get; init; } = 30.0;

    // --- 争奪戦（設計書04 §2.2「強豪との争奪戦イベントが発生し得る」） ---
    /// <summary>訪問1回あたりに競合校が動く確率。</summary>
    public double RivalEventProb { get; init; } = 0.22;
    /// <summary>競合校が動いたときに上がるティア段数（格が上がる＝成功率が下がる）。</summary>
    public int RivalEventTierStep { get; init; } = 1;
}

/// <summary>
/// 中学生リストの1人（設計書04 §2.2・issue #588）。実体は <see cref="DevelopingPlayer"/> で、
/// 勧誘に成功したら<b>そのまま翌4月の入部予定者へ合流する</b>（別の選手を作り直さない）。
/// </summary>
public sealed class RecruitProspect
{
    /// <summary>リスト内の安定した並び順（0始まり）。セーブと画面の対応キー。</summary>
    public int Index { get; set; }

    /// <summary>本人（合流時にそのままロスターへ入る）。</summary>
    public DevelopingPlayer Player { get; set; } = new();

    /// <summary>出身県（JIS順0基点）。</summary>
    public int PrefectureId { get; set; }

    /// <summary>自校から見た遠さ（確定時に焼く＝転任しない前提の現ビルドでは不変）。</summary>
    public RecruitDistance Distance { get; set; }

    /// <summary>競合校の格（<see cref="Tier"/>）。訪問で争奪戦が起きると上がる。</summary>
    public Tier RivalTier { get; set; } = Tier.G;

    /// <summary>これまでの訪問回数。</summary>
    public int Visits { get; set; }

    /// <summary>勧誘対象に指定しているか（枠を消費する）。</summary>
    public bool Targeted { get; set; }

    /// <summary>3月の判定を済ませたか。</summary>
    public bool Decided { get; set; }

    /// <summary>判定の結果（<see cref="Decided"/> が true のときだけ意味を持つ）。</summary>
    public bool Succeeded { get; set; }

    /// <summary>事前情報（スカウト網の段に依存する見立て。受動枠と同じ <see cref="ProspectScouting"/>）。</summary>
    public ProspectReport Report { get; set; } = new();
}

/// <summary>
/// 勧誘の進行状態（1年ぶん）。秋に作られ、3月の判定で使い切る。
/// </summary>
public sealed class RecruitingState
{
    /// <summary>このリストが対象にしている「合流する年度」（＝入部予定者と同じ年度）。</summary>
    public int YearIndex { get; set; }

    /// <summary>解禁されている枠数（寮の段から。0＝勧誘できない）。</summary>
    public int Slots { get; set; }

    /// <summary>中学生リスト（生成順＝安定順）。</summary>
    public List<RecruitProspect> Prospects { get; set; } = new();

    /// <summary>3月の判定を済ませたか（冪等ガード）。</summary>
    public bool Resolved { get; set; }

    /// <summary>いま勧誘対象にしている人数。</summary>
    public int TargetedCount
    {
        get
        {
            var n = 0;
            foreach (var p in Prospects) if (p.Targeted) n++;
            return n;
        }
    }
}

/// <summary>
/// 能動勧誘（設計書04 §2.2・issue #588）。中学生リストの生成・成功率・争奪戦・成否判定の純ロジック。
///
/// <para>乱数はすべて注入（不変条件#2）。呼び出し側は<b>受動枠の生成列とは別の Fork ストリーム</b>を
/// 渡すこと＝勧誘を何回試しても毎年の新入生は1ビットも動かない（<c>RecruitingTests</c> が固定）。</para>
/// </summary>
public static class RecruitModel
{
    /// <summary>
    /// 秋の中学生リストを作る。<paramref name="talentCenter"/> は受動枠と同じ中心値で、
    /// ここに <see cref="RecruitCoefficients.TalentCenterAdd"/> を足した値で引く。
    /// </summary>
    /// <param name="homePrefectureId">自校の本拠県（距離の起点）。</param>
    /// <param name="homeTier">自校の格（競合校の格を決める基準）。</param>
    public static RecruitingState BuildList(
        int yearIndex, int slots, int homePrefectureId, Tier homeTier,
        double talentCenter, RosterCoefficients roster, RecruitCoefficients c, IRandomSource rng,
        SkillCoefficients? skills = null, PersonalityCoefficients? personalities = null,
        FormCoefficients? form = null)
    {
        var state = new RecruitingState { YearIndex = yearIndex, Slots = Math.Max(0, slots) };
        if (c.ListSize <= 0) return state;

        // 本人は受動枠と同じレシピで作る（合流したときに「勧誘組だけ別物」にならない）。
        var players = ProspectGenerator.Intake(
            yearIndex, roster, rng, skills: skills, personalities: personalities,
            form: form, talentCenter: talentCenter + c.TalentCenterAdd, fixedCount: c.ListSize);

        for (var i = 0; i < players.Count; i++)
        {
            var prefectureId = RollPrefecture(homePrefectureId, c, rng);
            var p = new RecruitProspect
            {
                Index = i,
                Player = players[i],
                PrefectureId = prefectureId,
                Distance = PrefectureRegions.DistanceBetween(homePrefectureId, prefectureId),
                RivalTier = RollRivalTier(homeTier, rng),
            };
            state.Prospects.Add(p);
        }
        return state;
    }

    /// <summary>
    /// いまの成功率（設計書04 §2.2）。名声・距離（寮で緩和）・競合校の格・訪問回数から求める純関数。
    /// 画面はこの値をそのまま出す＝「何が効いているか」を隠さない。
    /// </summary>
    public static double SuccessProbability(
        RecruitProspect p, double fame, Tier homeTier, bool hasDorm, RecruitCoefficients c)
    {
        var prob = c.BaseProbability + c.ProbPerFame * Math.Max(0.0, fame);

        // 距離の減点。寮があるぶんだけ和らぐ（＝「距離は寮でカバー」）。
        var penalty = DistancePenalty(p.Distance, c);
        if (hasDorm) penalty *= 1.0 - MathUtil.Clamp(c.DormDistanceRelief, 0.0, 1.0);
        prob -= penalty;

        // 競合校の格。自校より上の段だけ効く（格下が競合しても怯まない）。
        var gap = TierRank(p.RivalTier) - TierRank(homeTier);
        if (gap > 0) prob -= c.ProbPerRivalTier * gap;

        // 通った回数。上限を置く＝通い詰めても確実にはならない。
        prob += Math.Min(c.VisitGainMax, c.ProbPerVisit * Math.Max(0, p.Visits));

        return MathUtil.Clamp(prob, c.ProbabilityMin, c.ProbabilityMax);
    }

    /// <summary>訪問1回の費用[万円]（距離で決まる。部費から出る）。</summary>
    public static double VisitCost(RecruitDistance d, RecruitCoefficients c) => d switch
    {
        RecruitDistance.SamePrefecture => c.VisitCostSamePrefecture,
        RecruitDistance.SameRegion => c.VisitCostSameRegion,
        RecruitDistance.NeighborRegion => c.VisitCostNeighborRegion,
        _ => c.VisitCostFar,
    };

    /// <summary>
    /// 1回訪問する。訪問回数を進め、確率で争奪戦（競合校の格が上がる）を起こす。
    /// <b>資金の引き落としと枠の判定は呼び出し側</b>（engine は金庫を持たない）。
    /// </summary>
    /// <returns>争奪戦が起きたら true。</returns>
    public static bool Visit(RecruitProspect p, RecruitCoefficients c, IRandomSource rng)
    {
        p.Visits++;
        if (!MathUtil.Chance(c.RivalEventProb, rng)) return false;

        // 競合校の格が上がる（S を超えては上がらない）。
        var rank = TierRank(p.RivalTier) + Math.Max(0, c.RivalEventTierStep);
        p.RivalTier = TierOfRank(Math.Min(rank, TierRank(Tier.S)));
        return true;
    }

    /// <summary>
    /// 3月の判定（設計書04 §2.1 の入部予定者確定と同じ週）。対象にしている中学生だけ成否を引く。
    /// 冪等（<see cref="RecruitingState.Resolved"/> が立っていれば何もしない）。
    /// </summary>
    /// <returns>勧誘に成功した中学生（合流順＝リスト順）。</returns>
    public static IReadOnlyList<RecruitProspect> Resolve(
        RecruitingState state, double fame, Tier homeTier, bool hasDorm,
        RecruitCoefficients c, IRandomSource rng)
    {
        var won = new List<RecruitProspect>();
        if (state.Resolved) return won;

        foreach (var p in state.Prospects)
        {
            if (!p.Targeted) continue;
            var prob = SuccessProbability(p, fame, homeTier, hasDorm, c);
            p.Decided = true;
            p.Succeeded = MathUtil.Chance(prob, rng);
            if (p.Succeeded) won.Add(p);
        }
        state.Resolved = true;
        return won;
    }

    // --- 補助 ---

    private static double DistancePenalty(RecruitDistance d, RecruitCoefficients c) => d switch
    {
        RecruitDistance.SamePrefecture => 0.0,
        RecruitDistance.SameRegion => c.PenaltySameRegion,
        RecruitDistance.NeighborRegion => c.PenaltyNeighborRegion,
        _ => c.PenaltyFar,
    };

    /// <summary>出身県を引く。自県が一定割合で出る（地元の子＝距離0）。</summary>
    private static int RollPrefecture(int homePrefectureId, RecruitCoefficients c, IRandomSource rng)
    {
        if (!MathUtil.Chance(MathUtil.Clamp(c.OutOfPrefectureRatio, 0.0, 1.0), rng)) return homePrefectureId;
        var count = PrefectureNames.Japanese.Count;
        var id = rng.NextInt(0, count);
        if (id == homePrefectureId) id = (id + 1) % count;   // 県外枠なので自県は避ける
        return id;
    }

    /// <summary>
    /// 競合校の格。自校と同格を中心に上下へ散らす（＝弱小校のリストに毎回Sが出たりしない）。
    /// </summary>
    private static Tier RollRivalTier(Tier homeTier, IRandomSource rng)
    {
        var roll = rng.NextDouble();
        var delta = roll < 0.35 ? 0
            : roll < 0.65 ? 1
            : roll < 0.85 ? 2
            : roll < 0.95 ? 3
            : -1;
        var rank = MathUtil.Clamp(TierRank(homeTier) + delta, 0, TierRank(Tier.S));
        return TierOfRank((int)rank);
    }

    // Tier は G(弱) → S(強) の順で並んでいないので、明示の順位表で扱う。
    private static readonly Tier[] Ladder = { Tier.G, Tier.F, Tier.E, Tier.D, Tier.C, Tier.B, Tier.A, Tier.S };

    /// <summary>ティアの強さ順位（G=0 … S=7）。</summary>
    public static int TierRank(Tier t)
    {
        for (var i = 0; i < Ladder.Length; i++) if (Ladder[i] == t) return i;
        return 0;
    }

    private static Tier TierOfRank(int rank) => Ladder[(int)MathUtil.Clamp(rank, 0, Ladder.Length - 1)];
}

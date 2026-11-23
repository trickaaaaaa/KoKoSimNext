using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;

namespace KokoSim.Engine.Career.Draft;

/// <summary>
/// 注目度スコアの算出（設計書20 §2）。能力の役割別合成（現在値×隠し上限のブレンド）と
/// 実績（<see cref="PlayerStats"/> 集計）の加重和。純関数・決定論・乱数不要（不変条件#2）。
/// 表示能力を直挿しせず合成＋隠し上限を混ぜる＝二層構造の趣旨に沿う（設計書20 §2.1）。
/// </summary>
public static class NotabilityModel
{
    // 役割別の能力加重（合計1.0）。投手加重は投手総合（PitcherComposite）と同じ重みを TeamStrengthCoefficients から引く
    // （球速単独で決まらない・重みの正典を1か所に保つ・issue #1212）。
    private static readonly (AbilityKind Kind, double Weight)[] BatterWeights =
    {
        (AbilityKind.Contact, 0.25), (AbilityKind.Power, 0.25), (AbilityKind.Speed, 0.15),
        (AbilityKind.Fielding, 0.15), (AbilityKind.ArmStrength, 0.10), (AbilityKind.Catching, 0.10),
    };
    private static (AbilityKind Kind, double Weight)[] PitcherWeights(TeamStrengthCoefficients strength) => new[]
    {
        (AbilityKind.Velocity, strength.VelocityWeight), (AbilityKind.Control, strength.ControlWeight),
        (AbilityKind.Stamina, strength.StaminaWeight), (AbilityKind.PitchRank, strength.PitchRankWeight),
    };

    /// <summary>選手の注目度（0〜100）。stats は未出場なら null 可（実績は中立50扱い）。</summary>
    public static double Compute(
        DevelopingPlayer player, PlayerStats? stats, DraftCoefficients c, TeamStrengthCoefficients strength)
    {
        var ability = AbilityScore(player, c, strength);
        var perf = PerformanceScore(player, stats, c);
        var n = c.AbilityWeight * ability + c.PerformanceWeight * perf;
        return Clamp(n, 0, 100);
    }

    /// <summary>
    /// 能力合成スコア（0〜100）。現在値と隠し上限を <see cref="DraftCoefficients.CeilingBlend"/> でブレンド。
    ///
    /// #366b: 選手の役割フラグで加重表を選ばず、**投手評価と打者評価の高い方**を採る。
    /// 役割はスカウトが判定するのであって選手が持つ属性ではない（設計書20 §2.1 改訂）。
    /// 投打とも強い個体は高い側で評価される＝二刀流が正しく注目される。
    /// </summary>
    public static double AbilityScore(DevelopingPlayer player, DraftCoefficients c, TeamStrengthCoefficients strength)
        => Math.Max(AbilityScoreWith(player, PitcherWeights(strength), c), AbilityScoreWith(player, BatterWeights, c));

    /// <summary>投手軸としての能力合成スコア（0〜100）。表示・比較用に個別で引けるようにしてある。</summary>
    public static double PitcherAbilityScore(DevelopingPlayer player, DraftCoefficients c, TeamStrengthCoefficients strength)
        => AbilityScoreWith(player, PitcherWeights(strength), c);

    /// <summary>打者軸としての能力合成スコア（0〜100）。</summary>
    public static double BatterAbilityScore(DevelopingPlayer player, DraftCoefficients c)
        => AbilityScoreWith(player, BatterWeights, c);

    private static double AbilityScoreWith(
        DevelopingPlayer player, (AbilityKind Kind, double Weight)[] weights, DraftCoefficients c)
    {
        var cur = 0.0;
        var cap = 0.0;
        foreach (var (kind, w) in weights)
        {
            // 注目度は今の物差し（表示Lv＋Δ・issue #1080）で合成する＝予想順位の境目（draft.yaml）はそのまま。
            cur += w * (player.Level(kind) + Players.AbilityScale.Delta);
            cap += w * (player.Cap(kind) + Players.AbilityScale.Delta);
        }
        var blend = Clamp(c.CeilingBlend, 0, 1);
        return Clamp((1 - blend) * cur + blend * cap, 0, 100);
    }

    /// <summary>
    /// 実績スコア（0〜100）。出場サンプルが少ないほど中立50へ収縮する。
    ///
    /// #366b: 役割で見る成績を決めず、**出場のあった軸**で評価する（両方あれば高い方）。
    /// 単純な max を採ると、出場が無い側の中立50が悪い成績を隠して全員が50以上に持ち上がるため、
    /// サンプルの有無で先に振り分けてから比べる。
    /// </summary>
    public static double PerformanceScore(DevelopingPlayer player, PlayerStats? stats, DraftCoefficients c)
    {
        if (stats is null) return 50.0;
        var pitching = PitchingPerformance(stats, c);
        var batting = BattingPerformance(stats, c);
        if (pitching is null && batting is null) return 50.0;
        if (pitching is null) return batting!.Value;
        if (batting is null) return pitching.Value;
        return Math.Max(pitching.Value, batting.Value);
    }

    /// <summary>投手成績からの実績スコア。登板が無ければ null（評価対象外）。</summary>
    private static double? PitchingPerformance(PlayerStats stats, DraftCoefficients c)
    {
        var p = stats.Pitching;
        if (p.BattersFaced <= 0) return null;
        var raw = 50.0
            + (c.PitcherEraBase - p.Era) * c.PitcherEraScale
            + (p.KPer9 - c.PitcherK9Base) * c.PitcherK9Scale;
        var shrink = Clamp((double)p.BattersFaced / Math.Max(1, c.PitcherMinBattersFaced), 0, 1);
        return Clamp(50.0 * (1 - shrink) + raw * shrink, 0, 100);
    }

    /// <summary>打撃成績からの実績スコア。打席が無ければ null（評価対象外）。</summary>
    private static double? BattingPerformance(PlayerStats stats, DraftCoefficients c)
    {
        var b = stats.Batting;
        if (b.PlateAppearances <= 0) return null;
        var hrPerGame = b.Games > 0 ? (double)b.HomeRuns / b.Games : 0.0;
        var raw = 50.0
            + (b.Ops - c.BatterOpsBase) * c.BatterOpsScale
            + hrPerGame * c.BatterHrPerGameScale;
        var shrink = Clamp((double)b.PlateAppearances / Math.Max(1, c.BatterMinPlateAppearances), 0, 1);
        return Clamp(50.0 * (1 - shrink) + raw * shrink, 0, 100);
    }

    /// <summary>注目度→予想指名順位バンド（設計書20 §3.1）。足切り（§3.1a）は含まない純粋な閾値写像。</summary>
    public static DraftRankBand BandOf(double notability, DraftCoefficients c)
    {
        if (notability >= c.FirstRoundThreshold) return DraftRankBand.FirstRound;
        if (notability >= c.UpperRoundThreshold) return DraftRankBand.UpperRound;
        if (notability >= c.MiddleRoundThreshold) return DraftRankBand.MiddleRound;
        if (notability >= c.CandidateThreshold) return DraftRankBand.LowerRound;
        return DraftRankBand.None;
    }

    /// <summary>
    /// 候補入りの足切り（設計書20 §3.1a）。選手画面のカテゴリランク（打撃力／投手力＝
    /// <see cref="PlayerStrengthProfile"/>・現在値だけの合成）の高い方が
    /// <see cref="DraftCoefficients.CandidateMinTier"/> に届いているか。注目度は隠し上限と成績で
    /// 持ち上がるので、これが無いと「打者ランクD」が中位で指名される。
    /// </summary>
    public static bool MeetsCandidateFloor(DevelopingPlayer player, TeamStrengthCoefficients strength, DraftCoefficients c)
    {
        // 足切りも今の物差しで判定（issue #1080）＝候補の顔ぶれは物差しによらず同じ（画面のチップは Δ 下がって見える）。
        var s = PlayerStrengthProfile.Compute(player, strength, Players.AbilityScale.Delta);
        var best = s.BattingTier > s.PitchingTier ? s.BattingTier : s.PitchingTier;
        return best >= c.CandidateMinTier;
    }

    /// <summary>
    /// 足切り込みの予想バンド。床に届かない選手・<see cref="DraftCoefficients.CandidateMinGrade"/> 未満の学年は
    /// 注目度に関わらず <see cref="DraftRankBand.None"/>。
    /// 候補判定はすべてここを通す（<see cref="DraftEngine"/> の3入口で共通）。
    /// </summary>
    public static DraftRankBand BandOf(
        DevelopingPlayer player, double notability, TeamStrengthCoefficients strength, DraftCoefficients c)
        => player.Grade >= c.CandidateMinGrade && MeetsCandidateFloor(player, strength, c)
            ? BandOf(notability, c)
            : DraftRankBand.None;

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}

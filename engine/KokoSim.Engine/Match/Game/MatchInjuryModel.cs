using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 試合中の場面駆動の受傷係数（設計書03 §3.5 拡張・設計書12）。
/// すべて data/coefficients.yaml の injury: から流し込む（不変条件#4）。
/// 各確率を 0 にするとその場面の判定へ一切入らず、乱数消費もゼロになる。
/// </summary>
public sealed record MatchInjuryCoefficients
{
    /// <summary>死球1回あたりの受傷率（打者）。</summary>
    public double HitByPitchProb { get; init; } = 0.010;
    /// <summary>本塁クロスプレー（バックホーム憤死）1回あたりの受傷率。</summary>
    public double HomeCollisionProb { get; init; } = 0.020;
    /// <summary>本塁クロスプレーで受傷したのが捕手側である割合（残りは走者）。</summary>
    public double HomeCollisionCatcherShare { get; init; } = 0.35;
    /// <summary>フェンス際の飛球を捕った外野手1回あたりの受傷率。</summary>
    public double FenceCrashProb { get; init; } = 0.030;
    /// <summary>「フェンス際」と見なす、着地点とフェンスまでの距離[m]。</summary>
    public double FenceCrashMarginM { get; init; } = 2.5;
    /// <summary>盗塁企図1回あたりの受傷率（スライディング）。</summary>
    public double SlidingProb { get; init; } = 0.0035;
    /// <summary>投球過多の投手が1打席ごとに受傷する率。</summary>
    public double OveruseProb { get; init; } = 0.008;
    /// <summary>投球過多と見なす、スタミナ目安球数の超過分[球]。</summary>
    public double OveruseOverPitches { get; init; } = 25.0;

    /// <summary>
    /// 怪我耐性（隠し, 50基準）1あたりの発生率減。週次判定と同じ掛け方をするため、
    /// YAML の injury.resistance_slope から InjuryCoefficients と同じ値を流し込む。
    /// </summary>
    public double ResistanceSlope { get; init; } = 0.010;
}

/// <summary>
/// 試合中に発生した怪我1件（観測データ。試合結果・乱数順には影響しない）。
/// 全治週は Season 層（InjuryModel.ToDiagnosis）で確定するのでここには持たない。
/// </summary>
public sealed record MatchInjuryEvent(
    int Inning,
    bool IsTop,
    InjuryScene Scene,
    string TeamName,
    string PlayerName,
    int? PlayerSourceId,
    int PlayerNumber,
    InjuryDraw Draw)
{
    public InjuryType Type => Draw.Type;
    public InjurySite Site => Draw.Site;
    public InjurySeverity Severity => Draw.Severity;

    /// <summary>タイムライン／通知フィード用の短文（傷病名はカタログ由来＝data 側が単一ソース）。</summary>
    public string Caption(InjuryCatalog catalog)
    {
        var name = catalog.DisplayName(Type);
        return name.Length > 0 ? PlayerName + " が負傷（" + name + "）" : PlayerName + " が負傷";
    }
}

/// <summary>
/// 試合中の受傷判定（設計書03 §3.5・issue #29 B）。
/// <para>
/// 決定論（不変条件#2）: 判定はすべて <see cref="IRandomSource.Fork"/> した専用ストリームで引く。
/// Fork は親ストリームの状態を進めないため、試合本体の乱数消費順・GameResult は完全に不変
/// （＝決定論ベースライン・統計帯は動かない）。受傷は当該試合の進行にも影響させず、
/// 記録だけを残して試合後に Season 層が選手状態へ反映する。
/// </para>
/// </summary>
public static class MatchInjuryModel
{
    /// <summary>受傷判定用の Fork ストリーム salt（1球采配・盗塁などと衝突しない値）。</summary>
    private const ulong Salt = 0x1E7A_0000UL;

    /// <summary>イニング・表裏・打席添字・場面（＋打席内の連番 sub）から決定論的に導く streamId。</summary>
    public static ulong StreamId(int inning, bool isTop, int paIndex, InjuryScene scene, int sub = 0)
        => Salt ^ ((ulong)(uint)inning << 48) ^ ((isTop ? 1UL : 0UL) << 47)
                ^ ((ulong)(uint)paIndex << 16) ^ ((ulong)(uint)sub << 8) ^ (ulong)(uint)(int)scene;

    /// <summary>
    /// 1件の受傷判定。<paramref name="baseProb"/> が 0 以下なら Fork も抽選も行わない（ゼロコスト・帯不変）。
    /// 発生率には怪我耐性・体質スキルの共通倍率（週次判定と同じ掛け方）が乗る。
    /// </summary>
    public static MatchInjuryEvent? Roll(
        InjuryScene scene, double baseProb, Player victim, string teamName,
        int inning, bool isTop, int paIndex,
        IRandomSource rng, MatchInjuryCoefficients c, SkillCoefficients? skills, InjuryCatalog catalog,
        int sub = 0)
    {
        if (baseProb <= 0.0 || catalog.IsEmpty) return null;

        var mult = Math.Max(0.1, 1.0 - (victim.InjuryResistance - 50) * c.ResistanceSlope);
        if (skills is not null)
        {
            if (victim.Skills.Has(Skill.Durable)) mult *= skills.DurableInjuryFactor;
            if (victim.Skills.Has(Skill.InjuryProne)) mult *= skills.InjuryProneInjuryFactor;
        }

        var stream = rng.Fork(StreamId(inning, isTop, paIndex, scene, sub));
        if (!MathUtil.Chance(MathUtil.Clamp(baseProb * mult, 0.0, 1.0), stream)) return null;

        if (catalog.Draw(scene, stream) is not { } draw) return null;
        return new MatchInjuryEvent(inning, isTop, scene, teamName,
            victim.Name, victim.SourceId, victim.UniformNumber, draw);
    }
}

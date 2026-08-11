using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;

namespace KokoSim.Engine.Season;

/// <summary>
/// 入部予定者の事前情報の係数（設計書04 §2.1 効果2・issue #587）。スカウト網の契約段が渡す
/// 開示精度 0〜1 を、①推定能力に乗る誤差σ ②隠しパラメータが判明する確率、の2つへ写す。
/// </summary>
public sealed record ProspectScoutingCoefficients
{
    /// <summary>開示精度0（未契約）のときの推定誤差σ（カテゴリ強さ0〜100スケール）。</summary>
    public double EstimateSdMax { get; init; } = 20.0;

    /// <summary>開示精度1のときの推定誤差σ。0にしない＝最上位でも「見立て」の域を出ない。</summary>
    public double EstimateSdMin { get; init; } = 4.0;

    /// <summary>開示精度0のときに隠しパラメータ1項目が判明する確率。</summary>
    public double RevealMin { get; init; } = 0.05;

    /// <summary>開示精度1のときに隠しパラメータ1項目が判明する確率。1にしない＝全部は見えない。</summary>
    public double RevealMax { get; init; } = 0.85;
}

/// <summary>
/// 入部予定者1人ぶんの事前情報（3月の確定時に1度だけ引き、入部まで変わらない）。
///
/// <para>推定値は<b>表示専用</b>で、能力そのものには一切触れない（＝スカウトの見立てが外れても
/// 入部する選手は変わらない）。4月に入部したら以後は実値を見る＝この報告書は役目を終える。</para>
/// </summary>
public sealed class ProspectReport
{
    /// <summary>
    /// <see cref="DevelopingPlayer.Id"/>。安定IDは合流時に振られるので、<b>確定時点では0</b>で、
    /// 4月に入部した直後に埋まる（入部後の「見立てと実際」の突き合わせに使う）。
    /// </summary>
    public int PlayerId { get; set; }

    /// <summary>予定者の氏名（合流後もロスターを引かずに一覧を出せるよう控えておく）。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 能動勧誘で獲った中学生か（issue #588）。受動枠で降ってきた子と区別して見せるための印で、
    /// 能力には一切影響しない。
    /// </summary>
    public bool Recruited { get; set; }

    // 推定カテゴリ強さ（0〜100。実値は PlayerStrengthProfile.Compute）。
    public double Batting { get; set; }
    public double Mobility { get; set; }
    public double Defense { get; set; }
    public double Pitching { get; set; }

    // 隠しパラメータが事前に判明しているか（判明しなければ画面は「？」のまま）。
    public bool KnowsGrowthType { get; set; }
    public bool KnowsPotential { get; set; }
    public bool KnowsPersonality { get; set; }
    public bool KnowsInjuryResistance { get; set; }

    /// <summary>隠しパラメータ4項目のうち、まだ「？」の数。少ないほど見通せている。</summary>
    public int UnknownCount =>
        (KnowsGrowthType ? 0 : 1) + (KnowsPotential ? 0 : 1)
        + (KnowsPersonality ? 0 : 1) + (KnowsInjuryResistance ? 0 : 1);

    /// <summary>推定4軸の最大値（一覧の並べ替え・「総合の見立て」に使う表示専用値）。</summary>
    public double Best
    {
        get
        {
            var v = Batting;
            if (Mobility > v) v = Mobility;
            if (Defense > v) v = Defense;
            if (Pitching > v) v = Pitching;
            return v;
        }
    }
}

/// <summary>
/// 入部予定者の下見（設計書04 §2.1・issue #587）。実能力にノイズを乗せた推定と、隠しパラメータの
/// 判明フラグを引く純関数。乱数は注入（不変条件#2）で、<b>本編の乱数列とは別ストリームで引くこと</b>
/// ＝見立ての抽選回数が変わっても生成される選手は1ビットも動かない。
/// </summary>
public static class ProspectScouting
{
    /// <summary>
    /// 予定者コホート全員ぶんの報告書を引く。<paramref name="disclosure"/> は
    /// <see cref="Career.ContractEffects.ProspectDisclosure"/>（0〜1）。
    /// </summary>
    public static List<ProspectReport> Survey(
        IReadOnlyList<DevelopingPlayer> cohort, double disclosure,
        TeamStrengthCoefficients strength, ProspectScoutingCoefficients c, IRandomSource rng)
    {
        var list = new List<ProspectReport>(cohort.Count);
        foreach (var p in cohort) list.Add(Survey(p, disclosure, strength, c, rng));
        return list;
    }

    /// <summary>予定者1人ぶんの報告書。乱数の消費順は固定（4軸→隠し4項目）＝決定論。</summary>
    public static ProspectReport Survey(
        DevelopingPlayer p, double disclosure,
        TeamStrengthCoefficients strength, ProspectScoutingCoefficients c, IRandomSource rng)
    {
        var d = MathUtil.Clamp(disclosure, 0.0, 1.0);
        // 開示が上がるほど誤差σは縮み、判明確率は上がる（どちらも線形補間）。
        var sd = c.EstimateSdMax + (c.EstimateSdMin - c.EstimateSdMax) * d;
        var reveal = c.RevealMin + (c.RevealMax - c.RevealMin) * d;

        var s = PlayerStrengthProfile.Compute(p, strength);
        return new ProspectReport
        {
            PlayerId = p.Id,
            Name = p.Name,
            Batting = Noisy(s.Batting, sd, rng),
            Mobility = Noisy(s.Mobility, sd, rng),
            Defense = Noisy(s.Defense, sd, rng),
            Pitching = Noisy(s.Pitching, sd, rng),
            KnowsGrowthType = MathUtil.Chance(reveal, rng),
            KnowsPotential = MathUtil.Chance(reveal, rng),
            KnowsPersonality = MathUtil.Chance(reveal, rng),
            KnowsInjuryResistance = MathUtil.Chance(reveal, rng),
        };
    }

    private static double Noisy(double actual, double sd, IRandomSource rng)
        => MathUtil.Clamp(actual + rng.NextGaussian(0, sd), 0.0, 100.0);
}

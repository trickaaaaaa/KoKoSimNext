using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Players;

/// <summary>調子（設計書02 §3.3）。パワプロ式5段階。内部は連続値（DevelopingPlayer.ConditionValue）。</summary>
public enum Condition
{
    Terrible,   // 絶不調
    Poor,       // 不調
    Normal,     // 普通
    Good,       // 好調
    Excellent,  // 絶好調
}

/// <summary>
/// 調子・試合限りの好不調の係数（設計書02 §3.3 / §3.3b）。🟡効果量は設計書07の検証で調整。
/// 「調子が支配的にならない」よう控えめが原則（能力の積み上げ > 運）。
/// </summary>
public sealed record FormCoefficients
{
    // --- 調子の効果量（1段階あたり。step = -2〜+2） ---
    public double ContactPerStep { get; init; } = 2.5;
    public double PowerPerStep { get; init; } = 2.0;
    public double ControlPerStep { get; init; } = 2.5;
    public double VelocityPerStepKmh { get; init; } = 0.6;
    /// <summary>キレ（PitchRank・球種ごとのSharpness）への補正（issue #49）。Controlと同スケール（1〜100軸）。</summary>
    public double SharpnessPerStep { get; init; } = 2.5;

    // --- 試合限りの好不調（§3.3b, dayForm ∈ [-1, +1]。調子より効果小さめ） ---
    /// <summary>通常日の揺らぎσ（大半は体感できないレベル）。</summary>
    public double DayFormBaseSigma { get; init; } = 0.15;
    /// <summary>通常日のクランプ（これを超えるのはスパイクのみ）。</summary>
    public double DayFormClamp { get; init; } = 0.45;
    /// <summary>「明確におかしい/走ってる日」の確率（十数試合に一度 ≒ 0.07）。</summary>
    public double DayFormSpikeProb { get; init; } = 0.07;
    public double DayFormSpikeMin { get; init; } = 0.6;
    public double DayFormSpikeMax { get; init; } = 1.0;
    public double ContactPerDayForm { get; init; } = 3.0;
    public double PowerPerDayForm { get; init; } = 2.0;
    public double ControlPerDayForm { get; init; } = 5.0;
    public double VelocityPerDayFormKmh { get; init; } = 1.5;
    /// <summary>キレへの当日補正（issue #49）。Controlと同スケール。</summary>
    public double SharpnessPerDayForm { get; init; } = 5.0;

    // --- 週次の波（§3.3: 数日〜数週間続く。毎試合振り直さない） ---
    /// <summary>前週の持ち越し（1に近いほど波が長い）。</summary>
    public double WeeklyPersistence { get; init; } = 0.75;
    public double WeeklySigma { get; init; } = 0.28;

    /// <summary>
    /// 初期 ConditionValue 抽選用の定常分布σ（issue #50）。AR(1) N(0, WeeklySigma²) を
    /// WeeklyPersistence で回し続けた定常状態の分散から導出する（WeeklySigma/WeeklyPersistenceの
    /// 変更に自動追従し、初期分布と週次の波を常に整合させる）。
    /// </summary>
    public double StationaryConditionSigma => WeeklySigma / Math.Sqrt(1 - WeeklyPersistence * WeeklyPersistence);

    // --- 相手校の調子観測（§3.3「監督の育成眼が高いほど正確、低いと誤認」, issue #47） ---
    /// <summary>育成眼0時の観測ノイズσ（連続値に加算後に量子化。段階幅0.35〜0.4なので2段階外す誤認もあり得る）。</summary>
    public double ObserveSigmaBase { get; init; } = 0.65;
    /// <summary>育成眼1あたりのσ減少（負値）。TalentEye=100で下限に達する＝ほぼ真値。</summary>
    public double ObserveSigmaPerTalentEye { get; init; } = -0.0063;
    /// <summary>σの下限（0にはしない＝育成眼MAXでも僅かな誤差は残す）。</summary>
    public double ObserveSigmaMin { get; init; } = 0.02;
}

/// <summary>
/// 調子・当日の出来を実効能力へ変換する（設計書02 §3.3 / §3.3b）。
/// 打席解決パイプラインの構造は変えず、能力補正として差し込む（PitchingFatigue.Effective と同型）。
/// 補正後も物理層変換（不変条件#1）を通るので、確率を直接いじらない。
/// </summary>
public static class FormModel
{
    /// <summary>調子5段階 → 補正ステップ（-2〜+2）。</summary>
    public static int Step(Condition c) => (int)c - (int)Condition.Normal;

    /// <summary>連続値（-1〜+1）→ 5段階（表示・投影用）。閾値は±0.55/±0.20。</summary>
    public static Condition Quantize(double v)
    {
        if (v >= 0.55) return Condition.Excellent;
        if (v >= 0.20) return Condition.Good;
        if (v > -0.20) return Condition.Normal;
        if (v > -0.55) return Condition.Poor;
        return Condition.Terrible;
    }

    /// <summary>打者へ適用（コンタクト・打球初速に補正, §3.3）。Normal＋dayForm=0 なら恒等。</summary>
    public static BatterAttributes ApplyBatter(BatterAttributes b, Condition c, double dayForm, FormCoefficients f)
    {
        var step = Step(c);
        if (step == 0 && dayForm == 0.0) return b;
        return b with
        {
            Contact = ClampAbility(b.Contact + step * f.ContactPerStep + dayForm * f.ContactPerDayForm),
            Power = ClampAbility(b.Power + step * f.PowerPerStep + dayForm * f.PowerPerDayForm),
        };
    }

    /// <summary>
    /// 投手へ適用（コントロールσ・球威=球速天井・キレに補正, §3.3/§3.3b）。
    /// キレは球種ごとの実値（<see cref="PitchSlot.Sharpness"/>）を補正する。ここが
    /// <see cref="Match.Pitching.PitchingCoefficients.SpinRpmPerSharpness"/> 経由で回転数（物理層）へ
    /// そのまま流れる（不変条件#1）。Repertoire が未指定（素質値 <see cref="PitcherAttributes.PitchRank"/>
    /// からの遅延フォールバックのみ）の場合はその素質値側を補正する。
    /// </summary>
    public static PitcherAttributes ApplyPitcher(PitcherAttributes p, Condition c, double dayForm, FormCoefficients f)
    {
        var step = Step(c);
        if (step == 0 && dayForm == 0.0) return p;
        var sharpnessDelta = step * f.SharpnessPerStep + dayForm * f.SharpnessPerDayForm;
        return p with
        {
            Control = ClampAbility(p.Control + step * f.ControlPerStep + dayForm * f.ControlPerDayForm),
            MaxVelocityKmh = Math.Max(105.0,
                p.MaxVelocityKmh + step * f.VelocityPerStepKmh + dayForm * f.VelocityPerDayFormKmh),
            PitchRank = ClampAbility(p.PitchRank + sharpnessDelta),
            Repertoire = p.Repertoire is null ? null : ApplySharpness(p.Repertoire, sharpnessDelta),
        };
    }

    private static IReadOnlyList<PitchSlot> ApplySharpness(IReadOnlyList<PitchSlot> repertoire, double delta)
    {
        var result = new PitchSlot[repertoire.Count];
        for (var i = 0; i < repertoire.Count; i++)
        {
            result[i] = repertoire[i] with { Sharpness = ClampAbility(repertoire[i].Sharpness + delta) };
        }
        return result;
    }

    /// <summary>
    /// 試合限りの好不調をサンプリング（§3.3b）。大半は微小（N(0,σ)を±clampに制限）、
    /// まれにスパイク（十数試合に一度、±0.6〜1.0）→「今日はどうもおかしい/やけに走っている」。
    /// 事前に見えない（UIには出さない）。
    /// </summary>
    public static double SampleDayForm(IRandomSource rng, FormCoefficients f, double varianceFactor = 1.0)
    {
        if (rng.NextDouble() < f.DayFormSpikeProb)
        {
            var sign = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
            var spike = sign * (f.DayFormSpikeMin + rng.NextDouble() * (f.DayFormSpikeMax - f.DayFormSpikeMin));
            // ムラっけ（設計書10）: 振れ幅を拡大。上限は±1.5に抑える（大崩れ/大当たりが極端になりすぎない）。
            return MathUtil.Clamp(spike * varianceFactor, -1.5, 1.5);
        }
        return MathUtil.Clamp(rng.NextGaussian(0, f.DayFormBaseSigma * varianceFactor),
            -f.DayFormClamp * varianceFactor, f.DayFormClamp * varianceFactor);
    }

    /// <summary>
    /// 週次の調子更新（§3.3: 平均回帰つきランダムウォーク。数週間続く波を作る）。
    /// イベント・試合結果による上下は設計書03のイベント側から加算する。
    /// </summary>
    public static double NextWeeklyCondition(double current, IRandomSource rng, FormCoefficients f)
    {
        var v = current * f.WeeklyPersistence + rng.NextGaussian(0, f.WeeklySigma);
        return MathUtil.Clamp(v, -1.0, 1.0);
    }

    /// <summary>
    /// 選手生成時の初期 ConditionValue 抽選（issue #50）。週次AR(1)の定常分布 N(0, σ_stationary²) から
    /// サンプリングする。全員 Normal(0) 固定ではなく、シーズン序盤・新チーム発足直後から個体差が出る。
    /// </summary>
    public static double SampleInitialCondition(IRandomSource rng, FormCoefficients f)
        => MathUtil.Clamp(rng.NextGaussian(0, f.StationaryConditionSigma), -1.0, 1.0);

    /// <summary>
    /// 相手校の調子観測（§3.3「監督の育成眼が高いほど正確に見える、低いと曖昧/誤認」, issue #47）。
    /// 真値 <paramref name="actualValue"/> に育成眼依存のσでガウスノイズを乗せてから5段階量子化する
    /// （連続値ノイズ→量子化方式。育成眼が低いほどσが大きく、隣接段階どころか2段階外す誤認もあり得る）。
    /// 呼び出し側が同一試合中は同じ rng（Fork 等で固定した状態）を渡すことで、観測結果が試合中ぶれない
    /// （決定論・不変条件#2）。自チームは常に真値なので Quantize を直接使う（本関数を呼ばない）。
    /// </summary>
    public static Condition Observe(double actualValue, double talentEye, IRandomSource rng, FormCoefficients f)
    {
        var sigma = Math.Max(f.ObserveSigmaMin, f.ObserveSigmaBase + talentEye * f.ObserveSigmaPerTalentEye);
        var noisy = actualValue + rng.NextGaussian(0, sigma);
        return Quantize(noisy);
    }

    /// <summary>
    /// 実効能力（調子補正後）。敵AI・委任采配のオーダー編成/代打判断（設計書11 §4, issue #48）が
    /// 「調子込みの実力」で候補を評価するための変換。<paramref name="perStep"/> は
    /// <see cref="FormCoefficients.ContactPerStep"/> 等を渡す想定（打席解決と同じ効果量を再利用）。
    /// 試合限りの好不調（dayForm）は事前に分からない情報のため含めない。
    /// </summary>
    public static int EffectiveAbility(int ability, Condition c, double perStep)
        => ClampAbility(ability + Step(c) * perStep);

    private static int ClampAbility(double v) => (int)MathUtil.Clamp(Math.Round(v), 1, 100);
}

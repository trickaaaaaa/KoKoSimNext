using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Batting;

/// <summary>スイング結果。</summary>
public enum ContactOutcome
{
    Whiff,
    Foul,
    InPlay,
}

/// <summary>
/// コンタクト判定と打球生成（設計書01 §2④⑤）。ミート vs 球威で空振り/ファウル/フェアを決め、
/// フェア時は初速・角度・方向を物理層で生成する。
/// </summary>
public static class ContactModel
{
    /// <summary>
    /// 空振り確率（設計書15 Phase E-2）。ミートが下げ、球威（腕の振り＋捕手リード）と
    /// 弾道由来の誘発変化合成量（<see cref="PitchTrajectoryFeatures.BreakMagnitudeM"/>）が上げる。
    /// ゾーン外スイングは当てにくい。テスト（物理妥当性の単調性）から直接呼べるよう Resolve と分離。
    /// </summary>
    public static double WhiffProbability(
        BatterAttributes batter,
        PitchPlan plan,
        PitchTrajectoryFeatures features,
        bool inZone,
        BattingCoefficients coeff,
        int familiarityPa = 0,
        double? pitchFamiliarityLevel = null)
    {
        // 誘発変化量の欺瞞効果は実球速で割り引く（issue #837・設計書21 §3.5）。遅い球ほど見る時間が長く、
        // 大きなホップに欺かれにくい＝球威・タイミング・打球質の球速項（反応球速で寝かせ済み）と対称に扱う。
        var effectiveBreak = features.BreakMagnitudeM * BreakDeceptionFactor(plan.VelocityKmh, coeff);

        // 投打差 d＝球威（腕の振り＋捕手リード）− ミート項。差の効きは soft-cap で飽和させる
        // （issue matchup-gap-saturation-k-rate）: 互角近傍は線形のまま、大差で頭打ち。
        var matchup = plan.Stuff - coeff.WhiffContactSlope * (batter.Contact - 50);
        var z = coeff.WhiffIntercept
                + SaturateMatchup(matchup, coeff.WhiffMatchupCap)
                + coeff.WhiffBreakSlope * effectiveBreak
                // 慣れ（設計書02 §2.3b）: 同一投手との対戦回数ぶん空振りにくくなる。変化量が大きい球ほど割引。
                - coeff.FamiliarityWhiffPerPa * FamiliarityEffect(familiarityPa, effectiveBreak, coeff)
                // 球種の慣れ（設計書02 §2.3b 案A・issue #836）: 同じ球種が連投されるほど空振りにくくなる。
                // 持ち球が少ない投手ほど自然に強く効く（追加の特徴量なし・慣れの蓄積だけで表現）。
                - coeff.PitchFamiliarityWhiffPerLevel * PitchFamiliarityEffect(pitchFamiliarityLevel, plan.PitchRank, coeff);
        if (!inZone)
        {
            z += coeff.WhiffOutOfZonePenalty;
        }
        return MathUtil.Logistic(z);
    }

    /// <summary>
    /// 誘発変化合成量[m]の欺瞞効果への実球速割引（issue #837・設計書21 §3.5）。基準球速
    /// <see cref="BattingCoefficients.BreakDeceptionRefKmh"/> 以上は満額（1.0）、それ未満は
    /// (実球速/基準球速)^<see cref="BattingCoefficients.BreakDeceptionExponent"/> まで減衰する。
    /// 指数0以下＝無効（従来どおり割引なし）。球速は<b>実球速</b>（<see cref="PitchPlan.VelocityKmh"/>）を読む＝
    /// 反応球速で寝かせた球威・タイミング・打球質の球速項とは別入口（物理の変化量自体は変えない）。
    /// </summary>
    public static double BreakDeceptionFactor(double velocityKmh, BattingCoefficients coeff)
    {
        if (coeff.BreakDeceptionExponent <= 0.0 || coeff.BreakDeceptionRefKmh <= 0.0) return 1.0;
        if (velocityKmh <= 0.0) return 0.0;
        var ratio = velocityKmh / coeff.BreakDeceptionRefKmh;
        var factor = System.Math.Pow(ratio, coeff.BreakDeceptionExponent);
        return factor < 1.0 ? factor : 1.0;
    }

    /// <summary>
    /// 慣れの実効蓄積（issue times-through-order-familiarity）: min(対戦回数, 上限) × (1 − 割引/m × 変化量)、下限0。
    /// 対戦回数0なら常に0＝慣れ無し（従来と1ビットも変わらない）。
    /// </summary>
    public static double FamiliarityEffect(int familiarityPa, double breakMagnitudeM, BattingCoefficients coeff)
    {
        if (familiarityPa <= 0) return 0.0;
        var n = System.Math.Min(familiarityPa, coeff.FamiliarityMaxPa);
        var discount = System.Math.Max(0.0, 1.0 - coeff.FamiliarityBreakDiscountPerM * breakMagnitudeM);
        return n * discount;
    }

    /// <summary>
    /// 球種の慣れ（設計書02 §2.3b 案A・issue #836）の効き。見慣れ度（<see cref="PitchFamiliarityTracker.Level"/>）が
    /// 基準（持ち球が平均的なときの定常値）からどれだけ離れているかに、球種ランクによる割引（キレ・球威が高い球は
    /// 慣れられても効きにくい＝§2.3b「慣れを万能にしない」）を掛ける。<paramref name="level"/> が null（トラッカー
    /// 未配線）なら常に0＝恒等。正＝持ち球が偏っていて見慣れられている（打者有利）、負＝持ち球が豊富で
    /// その球は相対的に見慣れていない（投手有利）。
    /// </summary>
    public static double PitchFamiliarityEffect(double? level, int pitchRank, BattingCoefficients coeff)
    {
        if (level is null) return 0.0;
        var discount = MathUtil.Clamp(coeff.PitchFamiliarityRankDiscountPerPoint * (pitchRank - 50), 0.0, 1.0);
        return (level.Value - coeff.PitchFamiliarityBaselineLevel) * (1.0 - discount);
    }

    /// <summary>
    /// 投打差の対数オッズ寄与の soft-cap: cap×tanh(d/cap)。cap≤0 なら恒等（従来の線形）。
    /// 奇関数なので平均同士（d≈0）の環境は動かさず、裾だけを潰す。
    /// </summary>
    public static double SaturateMatchup(double d, double cap)
        => cap > 0.0 ? cap * System.Math.Tanh(d / cap) : d;

    /// <summary>
    /// コンタクト品質への投手側の寄与（「打たせて取る」の土台）。打球初速・打球角のばらつきの双方を通じて
    /// 打球質を動かす。速い球は当たれば強い打球になり（本格派の代償）、制球とキレは芯を外させる（技巧派の武器）。
    /// 3項とも基準値で0＝リーグ平均投手では恒等なので、係数を入れても得点環境の平均は動かない。
    /// 球速は反応球速（<see cref="PitchPlan.ReactionKmh"/>＝効きを通した値）を読む（設計書21）。
    /// 変化量だけは欺瞞効果の実球速割引（<see cref="BreakDeceptionFactor"/>）を通した値を使う（issue #837）。
    /// </summary>
    public static double PitcherContactQualityBias(
        PitchPlan plan, PitchTrajectoryFeatures features, BattingCoefficients coeff)
        => coeff.ContactQualityPerKmh * (plan.ReactionKmh - coeff.ContactQualityVelocityRefKmh)
           + coeff.ContactQualityPerControl * (plan.ControlLevel - 50.0)
           + coeff.ContactQualityPerBreakM
               * (features.BreakMagnitudeM * BreakDeceptionFactor(plan.VelocityKmh, coeff) - coeff.ContactQualityBreakRefM);

    /// <summary>
    /// タイミングのズレの平均（設計書01 §2④, issue #262）。正＝差し込まれ（遅れ）／負＝早い。
    /// 速い球・変化の大きい球ほど遅れ、ミートが良いほどズレない。実測から直接引けるよう Resolve と分離。
    /// 変化量だけは欺瞞効果の実球速割引（<see cref="BreakDeceptionFactor"/>）を通した値を使う（issue #837）。
    /// </summary>
    public static double TimingOffsetMean(
        BatterAttributes batter, PitchPlan plan, PitchTrajectoryFeatures features, BattingCoefficients coeff)
        => coeff.TimingPerKmh * (plan.ReactionKmh - coeff.TimingRefKmh)
           + coeff.TimingPerBreakM
               * (features.BreakMagnitudeM * BreakDeceptionFactor(plan.VelocityKmh, coeff) - coeff.TimingBreakRefM)
           - coeff.TimingPerContact * (batter.Contact - 50);

    /// <summary>
    /// 打球方向[deg]（設計書01 §2⑤, issue #262）。−＝左翼（三塁側）／＋＝右翼（一塁側）。
    /// 引っ張り/流しの量は「タイミングのズレ」と「打球種別（打球角）」で決まり、打者の左右が符号を与える
    /// （右打者の引っ張り＝負／左打者の引っ張り＝正）。残差 <see cref="BattingCoefficients.BearingSigma"/> が
    /// それ以外のばらつき（打点の上下・スイング軌道）をまとめて受ける。
    /// </summary>
    public static double BearingDeg(
        BatterAttributes batter, double timingOffset, double launchAngleDeg,
        BattingCoefficients coeff, IRandomSource rng)
    {
        var pull = coeff.BearingPullPerTiming * -timingOffset + coeff.LaunchAnglePullDeg(launchAngleDeg);
        // スイッチは左打ち扱い（FieldingResolver の一塁到達ボーナスと同じ規約にそろえる）。
        var pullSign = batter.Bats is Handedness.Left or Handedness.Switch ? 1.0 : -1.0;
        return pullSign * pull + rng.NextGaussian(0, coeff.BearingSigma);
    }

    public static (ContactOutcome Outcome, BattedBall? Ball) Resolve(
        BatterAttributes batter,
        PitchPlan plan,
        PitchTrajectoryFeatures features,
        bool inZone,
        BattingCoefficients coeff,
        IRandomSource rng,
        int familiarityPa = 0,
        int strikes = 0,
        double? pitchFamiliarityLevel = null)
    {
        var whiffProb = WhiffProbability(batter, plan, features, inZone, coeff, familiarityPa, pitchFamiliarityLevel);
        if (MathUtil.Chance(whiffProb, rng))
        {
            return (ContactOutcome.Whiff, null);
        }

        // 当てたうち一定割合はファウル。
        // 追い込まれたらファールで粘る（issue #683）。
        var foulShare = strikes >= 2 ? Math.Min(0.9, coeff.FoulShare + coeff.TwoStrikeFoulBonus) : coeff.FoulShare;
        if (MathUtil.Chance(foulShare, rng))
        {
            return (ContactOutcome.Foul, null);
        }

        // フェア: コンタクト品質 → 初速・角度・方向。バレル(品質>1)で満初速を少し越える。
        // 投手側の寄与（球速/制球/キレ）が入るため、同じ打者でも「打たせて取る」投手には弱い打球になる。
        // 球種の慣れ（設計書02 §2.3b 案A・issue #836）: 同じ球種を連投されるほど打球の質も上がる。
        var quality = MathUtil.Clamp(
            coeff.QualityMean + coeff.QualityContactSlope * (batter.Contact - 50)
                + PitcherContactQualityBias(plan, features, coeff)
                + coeff.PitchFamiliarityQualityPerLevel * PitchFamiliarityEffect(pitchFamiliarityLevel, plan.PitchRank, coeff)
                + rng.NextGaussian(0, coeff.QualitySigma),
            QualityMin, 1.05);

        var maxExitKmh = coeff.ExitVeloInterceptKmh + batter.Power * coeff.ExitVeloPerPower;
        var veloFactor = VeloFactor(quality, coeff);
        var exitMps = maxExitKmh * veloFactor / 3.6;

        // 品質が低いほど角度が乱れる（ミスショットのゴロ・ポップ）。ミートが良いほど締まる（安打の分業。issue ai-roster-power-top-tier-all-sluggers）。
        var contactTighten = coeff.LaunchAngleSigmaPerContact > 0.0
            ? Math.Max(coeff.LaunchAngleSigmaContactFloor, 1.0 - coeff.LaunchAngleSigmaPerContact * (batter.Contact - 50))
            : 1.0;
        var angleSigma = coeff.LaunchAngleSigma * (1.0 + (1.0 - quality)) * contactTighten;
        // 球種の縦変化で角度が動く（issue #835）: 縦に落ちる球（フォーク等）ほど角度が下がりゴロが増える。
        var breakAngleShift = coeff.LaunchAnglePerVerticalBreakM * (features.InducedVerticalBreakM - coeff.LaunchAngleBreakRefM);
        var launchAngle = coeff.MeanLaunchAngle(batter.LaunchTendency) + breakAngleShift + rng.NextGaussian(0, angleSigma);

        // ④ タイミングのズレ（設計書01 §2④）→ ⑤ 打球方向（§2⑤）。issue #262 で案C＝ズレ変数を正式導入。
        var timing = TimingOffsetMean(batter, plan, features, coeff) + rng.NextGaussian(0, coeff.TimingSigma);
        var bearing = BearingDeg(batter, timing, launchAngle, coeff, rng);

        // ポップフライ（issue #830 ②）: 芯の下を叩いた当たりは真上に近く上がる遅い打球になる。品質が低いほど
        // なりやすい。方向は上で決めたまま。本流から Fork した専用ストリームで引く（Fork は親を進めない＝
        // ポップフライにならない打席の乱数・結果は1ビットも変わらない）。
        if (coeff.PopupShare > 0.0 && quality < coeff.PopupQualityMax && launchAngle >= coeff.PopupMinLaunchDeg)
        {
            var pop = rng.Fork(PopupStreamId);
            var weakness = (coeff.PopupQualityMax - quality) / (coeff.PopupQualityMax - QualityMin);
            var w = coeff.PopupWeaknessWeight;
            if (MathUtil.Chance(coeff.PopupShare * ((1.0 - w) + w * weakness), pop))
            {
                launchAngle = MathUtil.Clamp(pop.NextGaussian(coeff.PopupLaunchMeanDeg, coeff.PopupLaunchSigmaDeg), 50.0, 85.0);
                exitMps = Math.Max(45.0, pop.NextGaussian(coeff.PopupExitMeanKmh, coeff.PopupExitSigmaKmh)) / 3.6;
            }
        }

        var ball = new BattedBall
        {
            ExitVelocityMps = exitMps,
            LaunchAngleDeg = launchAngle,
            BearingDeg = bearing,
        };
        return (ContactOutcome.InPlay, ball);
    }

    /// <summary>ポップフライ（issue #830 ②）の Fork 用ストリーム番号。</summary>
    private const ulong PopupStreamId = 0x830B0B1UL;

    /// <summary>コンタクト品質の下限（クランプ値）。</summary>
    internal const double QualityMin = 0.05;

    /// <summary>
    /// コンタクト品質→初速係数（満初速に対する比）。基本は直線 min + (1−min)·品質。
    /// 品質が <see cref="BattingCoefficients.MishitQualityThreshold"/> 未満の裾（芯を外した当たり）だけ、
    /// 品質の下限で <see cref="BattingCoefficients.MishitVeloFactorFloor"/> になる直線へ折る（issue #759）。
    /// 閾値の点で値は連続。閾値0以下なら従来の直線と恒等。
    /// </summary>
    internal static double VeloFactor(double quality, BattingCoefficients coeff)
    {
        var line = coeff.MinQualityVeloFactor + (1.0 - coeff.MinQualityVeloFactor) * quality;
        var q0 = coeff.MishitQualityThreshold;
        if (q0 <= QualityMin || quality >= q0) return line;
        var atThreshold = coeff.MinQualityVeloFactor + (1.0 - coeff.MinQualityVeloFactor) * q0;
        var v = (quality - QualityMin) / (q0 - QualityMin);
        return coeff.MishitVeloFactorFloor + (atThreshold - coeff.MishitVeloFactorFloor) * v;
    }
}

namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 表示層→物理層の投球系変換係数（不変条件#1: 変換式の集約点）。
/// 値は KokoSim.Config が data/coefficients.yaml から生成して注入する。既定値は設計書02 §1.2 の初期値。
/// </summary>
public sealed record PitchingCoefficients
{
    /// <summary>コントロール→散布σ の切片[cm]（設計書02: 30 − C×0.22）。</summary>
    public double ControlSigmaInterceptCm { get; init; } = 30.0;

    /// <summary>コントロール→散布σ の傾き[cm/能力1]。</summary>
    public double ControlSigmaSlopeCmPerPoint { get; init; } = 0.22;

    /// <summary>散布σの下限[cm]（超高コントロールでも0にしない）。</summary>
    public double ControlSigmaMinCm { get; init; } = 2.0;

    /// <summary>
    /// コントロール表示値(1〜100)から本塁面での投球散布の標準偏差σ[m]を求める。
    /// 設計書02 §1.2: σ = 30 − C×0.22 [cm]（例: C50→19cm, C100→8cm）。
    /// </summary>
    public double ControlSigmaMeters(int control)
    {
        var cm = ControlSigmaInterceptCm - ControlSigmaSlopeCmPerPoint * control;
        if (cm < ControlSigmaMinCm)
        {
            cm = ControlSigmaMinCm;
        }
        return cm / 100.0;
    }

    // === 毎球の球速ばらつき（設計書02 §1.1d。最速=天井、常時は−3〜5km/h） 🟡 ===
    /// <summary>最速からの平均落差[km/h]。</summary>
    public double VelocityDropMeanKmh { get; init; } = 4.0;
    /// <summary>落差のσ[km/h]（小さいと安定、負側は天井でクランプ→たまに最速付近）。</summary>
    public double VelocityDropSigmaKmh { get; init; } = 1.8;
    /// <summary>落差の最大[km/h]（大暴落防止）。</summary>
    public double VelocityDropMaxKmh { get; init; } = 10.0;

    // === 配球（Phase 簡略。設計書09で采配に接続） ===
    /// <summary>ストレートを選ぶ基礎シェア（残りを変化球で等分）。</summary>
    public double StraightShare { get; init; } = 0.55;

    /// <summary>
    /// 捕手リード→球威の傾き（設計書01 §2①）。球威 += (Lead−50)×これ。
    /// 参考: 名捕手級リード100(+50)で ≈0.20（クセ球0.18相当）、平均50で 0。
    /// </summary>
    public double CatcherLeadStuffPerPoint { get; init; } = 0.004;

    // === 回転数マッピング（設計書15 §0.1 Q12-5, Phase B観測専用の暫定式） ===
    // Trajectory（弾道）は観測専用（判定に未接続, Phase E で本接続）。回転軸は全球種バックスピン固定
    // （横変化は現状モデル化しない）。キレ（Sharpness）だけを回転数へ反映し、球威（Power）は使わない。
    /// <summary>キレ(Sharpness=50)基準の回転数[rpm]（145km/h・2200rpm はCLAUDE.md検証値）。</summary>
    public double SpinRpmBase { get; init; } = 2200.0;
    /// <summary>キレ(Sharpness−50)あたりの回転数の傾き[rpm/能力1]。</summary>
    public double SpinRpmPerSharpness { get; init; } = 6.0;

    // === 投手ギア「飛ばす/流す」（設計書02 §1.1f）。エンジンフック（既定Normal） ===
    /// <summary>飛ばす: 球速天井ボーナス[km/h]。</summary>
    public double GearPushVelocityBonusKmh { get; init; } = 2.0;
    /// <summary>飛ばす: スタミナ消耗倍率。</summary>
    public double GearPushStaminaFactor { get; init; } = 1.6;
    /// <summary>流す: 球速天井ペナルティ[km/h]。</summary>
    public double GearCoastVelocityPenaltyKmh { get; init; } = 2.5;
    /// <summary>流す: スタミナ消耗倍率（温存）。</summary>
    public double GearCoastStaminaFactor { get; init; } = 0.7;
}

/// <summary>投手ギア（設計書02 §1.1f）。監督が能動的に選ぶ球威⇔スタミナのトレードオフ。</summary>
public enum PitcherGear
{
    Normal,
    Push,   // 飛ばしていけ
    Coast,  // 流していけ
}

using System;

namespace KokoSim.Engine.Players;

/// <summary>
/// 守備・走塁に関わる表示層能力（1〜100）。設計書02 §1.2 の変換式で物理量へ落とす。
/// </summary>
public sealed record FielderAttributes
{
    /// <summary>走力: スプリント速度 6.0 + R×0.025 m/s。守備範囲にも寄与。</summary>
    public int Speed { get; init; } = 50;

    /// <summary>肩（地肩）: 送球速度 90 + A×0.5 km/h。送球の「速さ」のみ（精度は ThrowAccuracy に分離, §1.2）。</summary>
    public int ArmStrength { get; init; } = 50;

    /// <summary>送球精度: 送球の正確さ。低いと悪送球・逸れが発生し中継/カバー連携を誘発（設計書01 §1.2）。</summary>
    public int ThrowAccuracy { get; init; } = 50;

    /// <summary>守備: 打球反応遅延 0.60 − D×0.004 s。初動・判断。</summary>
    public int Fielding { get; init; } = 50;

    /// <summary>捕球: エラー率。</summary>
    public int Catching { get; init; } = 50;

    /// <summary>
    /// 捕手リード（配球の質, 設計書01 §2①）。高いほど良い配球で球威を引き出す。
    /// 効果は物理（球威 Stuff への上乗せ）から出す＝(Lead−50)×係数。50=リーグ平均で恒等（帯不変, 不変条件#5）。
    /// 捕手以外では未使用（既定50）。
    /// </summary>
    public int Lead { get; init; } = 50;

    public static FielderAttributes LeagueAverage => new();

    /// <summary>スプリント速度[m/s]（設計書02 §1.2）。</summary>
    public double SprintSpeedMps => 6.0 + Speed * 0.025;

    /// <summary>送球速度[m/s]（設計書02 §1.2: 90 + A×0.5 km/h）。</summary>
    public double ThrowSpeedMps => (90.0 + ArmStrength * 0.5) / 3.6;

    /// <summary>打球反応遅延[s]（設計書02 §1.2）。</summary>
    public double ReactionDelaySeconds => 0.60 - Fielding * 0.004;

    /// <summary>
    /// 送球散布σ[cm]（設計書02 §1.2: 40 − Ac×0.36, 下限4cm）。
    /// 悪送球・逸れの大きさ。守備解決で中継/カバー連携・進塁を誘発する（2A では値のみ、適用は後続スライス）。
    /// </summary>
    public double ThrowScatterSigmaCm => Math.Max(4.0, 40.0 - ThrowAccuracy * 0.36);

    /// <summary>
    /// 送球トランスファー（捕球→送球の握り替え）倍率（設計書02 §1.2, Issue #36）。各トランスファー固定秒に
    /// 乗じる。守備(Fielding)50で×1.0＝現行固定値と恒等（帯不変, 不変条件#5。捕手 Lead と同方式）。守備が高い
    /// ほど短縮（速い＝倍率<1）、低いほど延長（遅い＝倍率>1）。傾き・下限倍率は YAML 駆動（不変条件#4）。
    /// </summary>
    public double TransferFactor(double slopePerPoint, double minFactor)
        => Math.Max(minFactor, 1.0 - (Fielding - 50) * slopePerPoint);
}

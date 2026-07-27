using System;

namespace KokoSim.Engine.Players;

/// <summary>全9球種（設計書02 §2.1）。物理層では球速比・回転数・回転軸で定義する。</summary>
public enum PitchType
{
    Fastball,   // ストレート
    TwoSeam,    // ツーシーム
    Cutter,     // カットボール
    Slider,     // スライダー
    Curve,      // カーブ
    Fork,       // フォーク
    Changeup,   // チェンジアップ
    Shuuto,     // シュート
    Sinker,     // シンカー
}

/// <summary>
/// 保有球種1つ（設計書02 §2.2）。ランクの内部は2軸（表示はランク1本に集約）:
/// ストレート: 球威(Power)→rpm→ホップ量 が有効軸。キレ(Sharpness)は将来拡張のために残す（未使用）。
/// 変化球: キレ(Sharpness)→rpm→変化量 が有効軸。球威(Power)は将来拡張のために残す（未使用）。
/// Issue #274 で2軸の役割分担を確定（design-02 §2.2 参照）。
/// </summary>
public sealed record PitchSlot
{
    public required PitchType Type { get; init; }

    /// <summary>球威（1〜100）。ストレートの有効軸（rpm → ホップ量）。変化球では未使用（フィールド保持のみ）。</summary>
    public required int Power { get; init; }

    /// <summary>キレ（1〜100）。変化球の有効軸（rpm → 変化量）。ストレートでは未使用（フィールド保持のみ）。</summary>
    public required int Sharpness { get; init; }

    /// <summary>
    /// 表示ランク（有効軸の値）。ストレートは球威(Power)、変化球はキレ(Sharpness)。
    /// Issue #274: 平均ではなく「その球種の質を担う軸そのもの」を使う。
    /// </summary>
    public int Rank => IsSpinByPower(Type) ? Power : Sharpness;

    /// <summary>rpm に使う軸が Power か（true=球威=ストレート系、false=キレ=変化球）。</summary>
    public static bool IsSpinByPower(PitchType type) => type == PitchType.Fastball;

    /// <summary>球速比（設計書02 §2.1）。実投球速度 = サンプリング球速 × これ。</summary>
    public double SpeedRatio => Type switch
    {
        PitchType.Fastball => 1.00,
        PitchType.TwoSeam => 0.98,
        PitchType.Cutter => 0.96,
        PitchType.Slider => 0.90,
        PitchType.Curve => 0.80,
        PitchType.Fork => 0.92,
        PitchType.Changeup => 0.85,
        PitchType.Shuuto => 0.95,
        PitchType.Sinker => 0.90,
        _ => 1.00,
    };

    /// <summary>指定ランクのストレート（全投手必修・野手の既定レパートリー）。</summary>
    public static PitchSlot FastballOf(int rank) => new()
    {
        Type = PitchType.Fastball,
        Power = Math.Clamp(rank, 1, 100),
        Sharpness = Math.Clamp(rank, 1, 100),
    };
}

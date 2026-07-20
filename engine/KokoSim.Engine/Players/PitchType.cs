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
/// 球威=押し込む力（変化球は回転数→変化量）、キレ=空振り誘発力（曲がり始めの遅さ/手元の伸び）。
/// ストレートも同じ体系を持つ（「球速150だが棒球」「140でも伸びる真っ直ぐ」を表現）。
/// </summary>
public sealed record PitchSlot
{
    public required PitchType Type { get; init; }

    /// <summary>球威（1〜100）。</summary>
    public required int Power { get; init; }

    /// <summary>キレ（1〜100）。</summary>
    public required int Sharpness { get; init; }

    /// <summary>表示ランクの内部値（2軸の平均）。G〜S の等級はこの値から引く。</summary>
    public int Rank => (Power + Sharpness) / 2;

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

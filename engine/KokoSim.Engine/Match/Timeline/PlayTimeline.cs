using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline;

/// <summary>
/// 平面座標[m]（本塁原点, +X=一塁側, +Z=センター方向。参照モックの [x,y] と同一規約）。
/// 高さは ApexHeightM 等の補助値で持ち、UI/3D が演出用に補間する。
/// </summary>
public readonly record struct TimelinePoint(double X, double Z);

/// <summary>ボール軌道セグメントの種別。</summary>
public enum BallSegmentKind
{
    Pitch,   // 投球（マウンド→本塁）
    Flight,  // 打球の滞空（接触→着地）
    Roll,    // ゴロ/バウンド（減速転がり）
    Throw,   // 送球（野手→野手/塁）
    Carry,   // 野手が保持して移動（タッチに向かう等）
}

/// <summary>
/// ボールの1セグメント（誰が・どこから・どこへ・何時に到達）。
/// UI は T0〜T1 を補間再生するだけ（物理計算はエンジン側で完結済み, CHANGELOG 32）。
/// </summary>
public sealed record BallSegment
{
    public required BallSegmentKind Kind { get; init; }
    public required double T0 { get; init; }
    public required double T1 { get; init; }
    public required TimelinePoint From { get; init; }
    public required TimelinePoint To { get; init; }
    /// <summary>滞空セグメントの頂点高さ[m]（Flight のみ意味を持つ。UIの放物線演出用）。</summary>
    public double ApexHeightM { get; init; }
}

/// <summary>野手1人の移動（守備陣形ルール表から自動付与, CHANGELOG 33）。</summary>
public sealed record FielderMove
{
    public required FieldPosition Role { get; init; }
    public required double T0 { get; init; }
    public required double T1 { get; init; }
    public required TimelinePoint To { get; init; }
    /// <summary>この移動の意図（打球処理/ベースカバー/中継/バックアップ）。UI表示・検証用。</summary>
    public required FielderTask Task { get; init; }
}

/// <summary>守備陣形ルール表の役割（設計書01 §2⑥）。</summary>
public enum FielderTask
{
    FieldBall,  // 打球処理
    CoverBase,  // ベースカバー
    Cutoff,     // 中継（カットマン）
    Backup,     // バックアップ
    Hold,       // 自位置で待機（明示）
}

/// <summary>走者1人の塁間レッグ（t0→t1 で from→to）。</summary>
public sealed record RunnerLeg
{
    /// <summary>走者の識別（"打"=打者走者, "走1"等）。UI表示用。</summary>
    public required string Label { get; init; }
    public required double T0 { get; init; }
    public required double T1 { get; init; }
    public required TimelinePoint From { get; init; }
    public required TimelinePoint To { get; init; }
    /// <summary>このレッグの終端でアウトになったか（タッチ/フォース）。</summary>
    public bool OutAtEnd { get; init; }
}

/// <summary>時刻付きキャプション（実況テキストのフック。文言はUI層が整形してもよい）。</summary>
public sealed record TimelineCaption(double T, string Text);

/// <summary>
/// 1プレーのタイムライン（エンジン出力契約, CHANGELOG 32）。
/// 「誰が・どこから・どこへ・何時に到達・何時に捕球/送球」を座標＋時刻で持ち、
/// UI は時刻 t を進めて再生するだけ。倍速・スロー・リプレイ・3D差し替えが全てこの上で成立する。
/// </summary>
public sealed record PlayTimeline
{
    /// <summary>プレー全体の長さ[s]。</summary>
    public required double Duration { get; init; }
    /// <summary>結果表示（"H レフト前" 等）。</summary>
    public required string Result { get; init; }
    /// <summary>結果が確定する時刻（スコア反映・演出の切れ目）。</summary>
    public required double ResolvedAt { get; init; }

    public IReadOnlyList<BallSegment> Ball { get; init; } = System.Array.Empty<BallSegment>();
    public IReadOnlyList<FielderMove> Moves { get; init; } = System.Array.Empty<FielderMove>();
    public IReadOnlyList<RunnerLeg> Runners { get; init; } = System.Array.Empty<RunnerLeg>();
    public IReadOnlyList<TimelineCaption> Captions { get; init; } = System.Array.Empty<TimelineCaption>();
}

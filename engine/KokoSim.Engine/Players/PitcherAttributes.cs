using System.Collections.Generic;

namespace KokoSim.Engine.Players;

/// <summary>投法（元祖踏襲の3種）。データの箱のみ保持し、補正ロジックは現フェーズ実装しない（設計書02 §2.2 / CHANGELOG 31b）。</summary>
public enum ThrowingStyle
{
    Overhand,
    Sidearm,
    Underhand,
}

/// <summary>
/// 投手の表示層能力。球速のみ km/h 直接表示（設計書02 §1.1）。
/// スタミナは等級でなく「目安投球数」で直接管理（§1.1e）。球種はストレート含む全てが2軸ランク（§2.2）。
/// </summary>
public sealed record PitcherAttributes
{
    /// <summary>最高球速[km/h]（範囲110〜165）。カタログ上限であり、試合中は毎球これ以下をサンプリング（§1.1d）。</summary>
    public double MaxVelocityKmh { get; init; } = 132.0;

    /// <summary>コントロール: 狙いからの散布σ（1〜100）。</summary>
    public int Control { get; init; } = 50;

    /// <summary>
    /// スタミナ＝目安投球数（§1.1e）。本来の力を保てる球数。他能力と独立に生成・管理。
    /// 内部の消耗カーブ（残量減→球速天井・制球低下）は PitchingFatigue が駆動する。
    /// </summary>
    public double StaminaPitches { get; init; } = 90.0;

    /// <summary>
    /// 球種総合の素質（1〜100, 育成レイヤ）。レパートリー各球のランクの土台。
    /// 表示は球種ごとのランク（Repertoire）で行い、本値は成長・投影の中間表現。
    /// </summary>
    public int PitchRank { get; init; } = 50;

    /// <summary>
    /// 保有球種（設計書02 §2.2）。全投手ストレート必修。野手はデフォルト0〜1変化球（＝ストレート＋最大1）。
    /// 未指定時はストレートのみ（ランク=PitchRank）。
    /// </summary>
    public IReadOnlyList<PitchSlot>? Repertoire { get; init; }

    public ThrowingStyle Style { get; init; } = ThrowingStyle.Overhand;

    /// <summary>実効レパートリー（未指定ならストレートのみ）。</summary>
    public IReadOnlyList<PitchSlot> EffectiveRepertoire
        => Repertoire is { Count: > 0 } r ? r : new[] { PitchSlot.FastballOf(PitchRank) };

    /// <summary>育成レイヤのスタミナLevel(1〜100)→目安投球数の変換（表示層→内部の一箇所集約）。</summary>
    public static double StaminaPitchesFromLevel(int level) => 45.0 + level * 0.9;

    /// <summary>リーグ平均的な投手（球速132km/h, 各50 = D帯, 目安90球）。</summary>
    public static PitcherAttributes LeagueAverage => new();
}

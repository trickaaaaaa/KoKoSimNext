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

    /// <summary>
    /// 球質タイプ（本格派/技巧派/軟投派/バランス）。生成時に確定した型をそのまま保持する純データ。
    /// null＝型を持たずに作られた投手（育成選手からの投影など）。表示側は null なら能力から推定する。
    /// 試合結果には影響しない（型の効果は既に球速・制球・キレのレベルへ焼き込み済み）。
    /// </summary>
    public PitcherArchetype? Archetype { get; init; }

    /// <summary>実効レパートリー（未指定ならストレートのみ）。</summary>
    public IReadOnlyList<PitchSlot> EffectiveRepertoire
        => Repertoire is { Count: > 0 } r ? r : new[] { PitchSlot.FastballOf(PitchRank) };

    /// <summary>育成レイヤのスタミナLevel(1〜100)→目安投球数の変換（表示層→内部の一箇所集約）。</summary>
    public static double StaminaPitchesFromLevel(int level) => 45.0 + level * 0.9;

    /// <summary>目安投球数→スタミナLevel の逆変換（投手総合の合成で他能力と土俵を揃える用）。</summary>
    public static int LevelFromStaminaPitches(double pitches)
        => (int)Math.Round(Math.Clamp((pitches - 45.0) / 0.9, 1, 100));

    // --- 球速Level → 最高球速[km/h]（表示層→物理層の一箇所集約・不変条件#1） ---
    // 旧式 120 + Lv*0.45 は Lv50→142.5km/h と高校野球として速すぎ、かつ Lv80以上が上限155に張り付いて
    // 最上位帯の差が消えていた（本格派が際立たない）。下の式は LeagueAverage の記述（Lv50=132km/h）と一致し、
    // 上限に触れずに Lv99≈155 まで伸びる。目安: Lv30→123 / Lv50→132 / Lv70→141 / Lv85→148 / Lv99→155。
    private const double VelocityInterceptKmh = 108.5;
    private const double VelocitySlopeKmhPerLevel = 0.47;

    /// <summary>育成レイヤの球速Level(1〜100)→最高球速[km/h]の変換。</summary>
    public static double VelocityKmhFromLevel(int level)
        => VelocityInterceptKmh + level * VelocitySlopeKmhPerLevel;

    /// <summary>最高球速[km/h]→球速Level の逆変換（表示・選出で「球速の等級」を復元する用）。</summary>
    public static int LevelFromVelocityKmh(double kmh)
    {
        var lv = (kmh - VelocityInterceptKmh) / VelocitySlopeKmhPerLevel;
        return (int)Math.Round(Math.Clamp(lv, 1, 100));
    }

    /// <summary>リーグ平均的な投手（球速132km/h, 各50 = D帯, 目安90球）。</summary>
    public static PitcherAttributes LeagueAverage => new();
}

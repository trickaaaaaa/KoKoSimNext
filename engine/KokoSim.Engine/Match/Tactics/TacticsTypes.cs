using System.Collections.Generic;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>攻撃サイン（設計書09 §1）。1打席の作戦指示。既定は強攻。</summary>
public enum OffensiveSign
{
    Swing,          // 強攻
    Take,           // 待て（初球見送り）
    SacrificeBunt,  // 送りバント
    SafetyBunt,     // セーフティバント
    Squeeze,        // スクイズ
    Steal,          // 盗塁
    HitAndRun,      // エンドラン
    Buster,         // バスター
}

/// <summary>
/// 走者のスタート種別（設計書12 §4, Q10決定）。OffensiveSignの拡張ではなく独立の軸として走者へ付与する
/// （攻撃サイン×守備状態×打球結果の掛け算に忠実であるため。サインの組み合わせ爆発を避ける）。
/// 効果は「実効スタート時刻の前倒し」と「戻れる/戻れないの reversibility」だけで表現する（二層構造）。
/// </summary>
public enum StartType
{
    /// <summary>通常: 反応後に判断して走る。打球が空中で捕られても余裕を持って塁へ戻れる。</summary>
    Normal,
    /// <summary>コンタクト（ゴロゴー/エンドラン）: 接触と同時に飛び出す＝戻れない前提のコミット。
    /// ゴロなら先の塁を陥れやすいが、打球が空中で捕られると塁へ戻れず併殺の危機（G2, ライナー併殺）。</summary>
    Contact,
    /// <summary>ギャンブル: 投球と同時（最高のスタート）。盗塁の好ジャンプ・単打で二塁から還れる一方、
    /// 守備に読まれると最も無防備（G3: 守備の読み/ピッチアウトと合わせて配線）。</summary>
    Gamble,
}

/// <summary>配球方針（設計書09 §2.2）。自動配球への重み付け。</summary>
public enum PitchPolicy
{
    Auto,           // おまかせ
    FastballHeavy,  // 直球中心
    BreakingHeavy,  // 変化球中心
    ControlFirst,   // コントロール重視（ゾーン内比率↑）
    KeepLow,        // 低め徹底
    InsideAttack,   // 強気の内角
}

/// <summary>守備陣形の深さ（設計書09 §2.1: 前進 / 普通 / 後退）。</summary>
public enum DefenseDepth
{
    In,
    Normal,
    Deep,
}

/// <summary>守備指示一式（設計書09 §2）。方針は常時設定・随時変更。</summary>
public sealed record DefensiveTactics
{
    public DefenseDepth Infield { get; init; } = DefenseDepth.Normal;
    public DefenseDepth Outfield { get; init; } = DefenseDepth.Normal;
    /// <summary>バントシフト（一・三塁手チャージ）。初期守備位置を差し替える＝効果は物理から出る。</summary>
    public bool BuntShift { get; init; }
    public PitchPolicy Policy { get; init; } = PitchPolicy.Auto;
    public PitcherGear Gear { get; init; } = PitcherGear.Normal;
    /// <summary>敬遠（design-14 P1-3）。真なら次打者と勝負するため、この打席は無条件四球（申告制＝投球数0）。</summary>
    public bool IntentionalWalk { get; init; }

    /// <summary>指示なし（現状の試合挙動と完全一致）。</summary>
    public static DefensiveTactics Default { get; } = new();
}

/// <summary>
/// 采配判断に渡す試合状況のスナップショット。ScoreDiff は攻撃側から見た得失点差（正=リード）。
/// プレイヤーUI・敵AI（設計書11）・委任采配のすべてが同じ型を受け取る。
/// </summary>
public readonly record struct TacticsSituation(
    int Inning,
    int RegulationInnings,
    int Outs,
    int ScoreDiff,
    Player? OnFirst,
    Player? OnSecond,
    Player? OnThird,
    Player Batter,
    Player Pitcher,
    Player Catcher,
    int PressureIndex,
    bool PitcherRattled,
    int OffenseTimeoutsLeft,
    int DefenseTimeoutsLeft,
    bool TieBreak);

/// <summary>
/// 選手交代の判断に渡す状況（設計書09 §6）。ScoreDiff は判断するチーム視点の得失点差（正=リード）。
/// 攻撃側=代打（UpcomingBatter）/代走（塁上走者）、守備側=守備固め（Lineup から選ぶ）。
/// Lineup は現在の打順9人、Bench は起用できる控え（いずれも交代反映済み）。
/// </summary>
public readonly record struct SubstitutionSituation(
    int Inning,
    int RegulationInnings,
    int Outs,
    int ScoreDiff,
    Player? OnFirst,
    Player? OnSecond,
    Player? OnThird,
    Player UpcomingBatter,
    bool UpcomingBatterIsPitcher,
    IReadOnlyList<Player> Lineup,
    IReadOnlyList<Player> Bench);

/// <summary>
/// 継投判断に渡す状況（issue #209, 設計書11 §4）。守備側（＝投げている側）視点で組む。
/// <see cref="FatigueTriggered"/>／<see cref="AtWeeklyLimit"/> は試合エンジンが従来と同じ式で
/// 事前計算した「基本トリガー」で、<see cref="StandardTacticsBrain"/> はこれをそのまま尊重して
/// 従来挙動と1ビットも変わらない（恒等）。高度トリガー（崩れ・僅差終盤・動揺）用に生の球数・
/// 得失点差・イニング内失点も併せて渡す（<see cref="AiTacticsBrain"/> のみが参照）。
/// </summary>
public readonly record struct PitchingChangeSituation(
    int Inning,
    int RegulationInnings,
    int Outs,
    int DefenseScoreDiff,        // 守備側視点の得失点差（正=リード）
    int RunsAllowedThisInning,   // 当該半イニングの失点（崩れ検知）
    bool PitcherRattled,         // 連続出塁による動揺（設計書09）
    bool FatigueTriggered,       // PitchingFatigue.ShouldRelieve の結果（疲労margin/ハードキャップ）
    bool AtWeeklyLimit,          // 週間球数制限に到達
    double FatiguePitches,       // ギア重み込みの実効消費球数
    double StaminaTarget,        // 現投手の目安投球数（StaminaPitches）
    double RelieveMargin,        // 継投しきい値の余白（FatigueCoefficients.RelievePitchMargin）
    bool ReliefAvailable);       // 交代できる控え投手がいるか

/// <summary>継投が発火した理由（テスト・観測用。挙動には影響しない）。</summary>
public enum PitchingChangeReason
{
    Fatigue,      // 疲労球数（従来トリガー）
    WeeklyLimit,  // 週間球数制限（従来トリガー）
    Blowup,       // 崩れ（イニング内の大量失点）
    CloseLate,    // 僅差終盤（点差×イニング）
    Rattled,      // 動揺（連続出塁）
}

/// <summary>
/// 継投判断の結果（issue #209）。null=続投。Phase A では人選は既存の bullpen 先頭のままで、
/// この決定は「替えるか否か＋理由」だけを表す（人選＝横断ランキングは Phase B で拡張）。
/// </summary>
public readonly record struct PitchingChangeDecision(PitchingChangeReason Reason);

/// <summary>
/// 配球方針が自動配球（PitchSelection）へ与える重み（設計書09 §2.2）。
/// 全て0/1.0なら恒等＝おまかせ。効果はゾーン内比率・狙い位置という物理入力に出る（二層構造の維持）。
/// </summary>
public readonly record struct PitchDirective(
    double StraightShareDelta,
    double AimXOffsetM,
    double AimYOffsetM,
    double AimSigmaFactor)
{
    public static PitchDirective Identity => new(0.0, 0.0, 0.0, 1.0);
}

using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.AtBat;

/// <summary>
/// 打席解決に必要な環境・係数一式。係数群は KokoSim.Config が YAML から生成して注入する（不変条件#1/#4）。
/// </summary>
public sealed record AtBatContext
{
    public Aerodynamics Aerodynamics { get; init; } = new();
    public MoundGeometry Mound { get; init; } = new();
    public StrikeZone StrikeZone { get; init; } = new();
    public FieldGeometry Field { get; init; } = new();
    public PitchingCoefficients Pitching { get; init; } = new();
    public BattingCoefficients Batting { get; init; } = new();
    public FieldingCoefficients Fielding { get; init; } = new();
    /// <summary>バント（設計書15 Phase D-2b）等、走塁系の解決に使う係数。</summary>
    public BaserunningCoefficients Baserunning { get; init; } = new();

    /// <summary>守備配置（既定は league-average の標準配置）。</summary>
    public IReadOnlyList<Fielder> Fielders { get; init; } = new FieldGeometry().StandardAlignment();

    /// <summary>投手ギア「飛ばす/流す」（設計書02 §1.1f）。采配（設計書09）が設定する。既定Normal。</summary>
    public PitcherGear Gear { get; init; } = PitcherGear.Normal;

    /// <summary>配球方針の重み（設計書09 §2.2）。null=おまかせ（従来と完全一致）。</summary>
    public Tactics.PitchDirective? Directive { get; init; }

    /// <summary>「待て」サイン（設計書09 §1）: 初球を必ず見送る。</summary>
    public bool TakeFirstPitch { get; init; }

    /// <summary>
    /// 敬遠（design-14 P1-3・設計書15 Phase D-2a）: 真なら最初の <see cref="AtBat.AtBatSession.ThrowNextPitch"/>
    /// で投球ループ自体をスキップし、投球数0・RNG非消費のまま四球確定する（2026年ベースライン＝常時申告制）。
    /// </summary>
    public bool IntentionalWalk { get; init; }

    /// <summary>守備側の捕手リード（設計書01 §2①）。試合エンジンが捕手から設定。既定50=平均で恒等。</summary>
    public int CatcherLead { get; init; } = 50;

    /// <summary>スキルによる1打席の挙動補正（設計書10）。既定は無効果（スキルなしと同一）。</summary>
    public SkillPlayMods Skills { get; init; } = SkillPlayMods.None;

    /// <summary>タイムライン出力（CHANGELOG 32）。既定オフ＝統計シムはゼロコスト。UI再生時のみオン。</summary>
    public bool CaptureTimeline { get; init; }

    /// <summary>
    /// 1球の構造化トレース（設計書17 §4, F1）。既定オフ＝統計シムはゼロコスト。
    /// オンでも新たな抽選は一切せず、解決済みの値と純関数の値を写すだけ＝乱数消費は不変。
    /// </summary>
    public bool CaptureTrace { get; init; }

    /// <summary>守備陣形ルール表（CHANGELOG 33）。null なら既定表。</summary>
    public Match.Timeline.FormationTable? Formations { get; init; }

    /// <summary>塁上に走者がいるか（陣形ルールのキー）。試合エンジンが打席ごとに設定。</summary>
    public bool RunnersOn { get; init; }
}

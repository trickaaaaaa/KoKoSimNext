using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Debugging;

/// <summary>
/// 1球の観測レコード（設計書17 §4.1, F1）。<b>結果を1ビットも変えない</b>観測データで、
/// 解決済みの値の写しと、既に純関数で求まる値（スイング確率など）だけを持つ。新たな抽選は一切しない。
///
/// <para><see cref="PitchRecord"/> は<b>触らない</b>（決定論ゲート <c>GameResultDigest</c> の正規化対象なので、
/// 拡張すると再ベースラインが要る）。本レコードは別枠で <see cref="IDebugTraceSink"/> へ流すだけで、
/// <see cref="GameResult"/> にも digest にも載らない。</para>
///
/// <para>設計書17 §4.1 のスケッチは <c>readonly record struct</c> の位置引数だが、30近いフィールドを
/// 打席解決層（意図・実着弾・打者判断）と試合層（局面・状態・采配・RNG）の2箇所から埋めるため、
/// init 可能なプロパティを持つ参照型にした。生成は <c>CaptureTrace</c> が真のときだけ走る。</para>
/// </summary>
public sealed record PitchTrace
{
    // ── 局面（試合層が埋める） ──
    public int Inning { get; init; }
    public bool IsTop { get; init; }
    public int Outs { get; init; }
    /// <summary>打者の識別子（SourceId があれば "#id"、無ければ名前）。</summary>
    public string BatterId { get; init; } = "";
    public string PitcherId { get; init; } = "";
    public int BallsBefore { get; init; }
    public int StrikesBefore { get; init; }
    /// <summary>この打席で何球目か（1始まり）。</summary>
    public int PitchNoInPa { get; init; }
    /// <summary>この試合で何球目か（1始まり・両軍通算）。</summary>
    public int PitchNoInGame { get; init; }

    // ── 意図（PitchPlan・打席解決層が埋める） ──
    public PitchType PlanType { get; init; }
    public double PlanAimX { get; init; }
    public double PlanAimY { get; init; }
    public double PlanVelocityKmh { get; init; }
    public double PlanStuff { get; init; }

    // ── 実着弾（ControlScatter後）＋弾道特徴（TrajectoryFeatureTable） ──
    public double ActualX { get; init; }
    public double ActualY { get; init; }
    public double ActualKmh { get; init; }
    public double FlightTimeSeconds { get; init; }
    public double InducedVerticalBreakM { get; init; }
    public double InducedHorizontalBreakM { get; init; }
    public bool InZone { get; init; }

    // ── 打者判断（BatterDecision） ──
    /// <summary>この球のスイング確率。純関数なので観測しても乱数を1回も追加消費しない。</summary>
    public double SwingProbability { get; init; }
    public bool Swung { get; init; }

    // ── 結果 ──
    public PitchKind Kind { get; init; }

    // ── 打球（InPlay のときのみ） ──
    public double? ExitVelocityKmh { get; init; }
    public double? LaunchAngleDeg { get; init; }
    public double? SprayAngleDeg { get; init; }

    // ── 状態（試合層が埋める） ──
    public int PressureIndex { get; init; }
    public bool Rattled { get; init; }
    /// <summary>この投手の当日の累計球数（PitchingFatigue の入力）。</summary>
    public int PitchingFatigue { get; init; }
    public PitcherGear Gear { get; init; }
    public PitchPolicy Policy { get; init; }

    // ── 采配（AI/委任が動いたときのみ） ──
    public string? ChosenSign { get; init; }
    /// <summary>候補スコア上位（"Steal:0.61,Normal:0.55" 形式）。F3 で <see cref="ITacticsBrain"/> 側から埋まる。</summary>
    public string? SignCandidatesCsv { get; init; }

    // ── RNG ──
    /// <summary>この球で最後に Fork された派生ストリームのid（Forkしていなければ0）。</summary>
    public ulong RngStreamId { get; init; }
    /// <summary>この球の解決で消費した乱数の本数（Fork先の消費も含む）。</summary>
    public int RngDrawsInPitch { get; init; }

    // ── 注入（F4） ──
    /// <summary>強制発動（<c>ForcedOutcome</c>）でこの球の抽選をスキップしたか。</summary>
    public bool Forced { get; init; }
}

/// <summary>1打席の観測レコード（設計書17 §4.1）。</summary>
public sealed record PaTrace
{
    public int Inning { get; init; }
    public bool IsTop { get; init; }
    public string BatterId { get; init; } = "";
    public PlateAppearanceResult Result { get; init; }
    public int Rbi { get; init; }
    public int OutsAfter { get; init; }
    public int Pitches { get; init; }
    /// <summary>この打席で走者が動いた要約（タイムラインがあるときだけ・無ければ空）。</summary>
    public string RunnerSummary { get; init; } = "";
    public bool Forced { get; init; }
}

/// <summary>試合1件の観測ヘッダ（設計書17 §4.1）。再現に必要な最小情報を先頭1行に置く。</summary>
public sealed record GameTraceHeader
{
    public string AwayName { get; init; } = "";
    public string HomeName { get; init; } = "";
    /// <summary>開始時点の RNG 内部状態（16進連結）。これ単体で試合を頭から再生できる。</summary>
    public string RngStateHex { get; init; } = "";
    /// <summary>対戦カード指紋（<see cref="ReproToken.Fingerprint"/>）。</summary>
    public string FixtureFingerprint { get; init; } = "";
    /// <summary>注入シナリオid（設計書17 §3.4）。通常の試合は null。</summary>
    public string? ScenarioId { get; init; }
    public int RegulationInnings { get; init; }
    public bool TieBreakEnabled { get; init; }
    public bool MercyRuleEnabled { get; init; }
}

/// <summary>
/// 観測の出口（設計書17 §4.2）。engine は<b>ここへ渡すだけ</b>で、ファイル書き込みは
/// Balance CLI（<c>JsonlTraceSink</c>）／Unity（<c>RingBufferTraceSink</c>）側が担う（不変条件#3）。
/// </summary>
public interface IDebugTraceSink
{
    void OnGameStart(GameTraceHeader header);
    void OnPitch(PitchTrace t);
    void OnPlateAppearance(PaTrace t);
    void OnGameEnd(GameResult result);
}

/// <summary>複数のシンクへ同じ観測を配る（HUDとJSONLを同時に使うとき）。</summary>
public sealed class CompositeTraceSink : IDebugTraceSink
{
    private readonly IReadOnlyList<IDebugTraceSink> _sinks;
    public CompositeTraceSink(params IDebugTraceSink[] sinks) => _sinks = sinks;

    public void OnGameStart(GameTraceHeader header) { foreach (var s in _sinks) s.OnGameStart(header); }
    public void OnPitch(PitchTrace t) { foreach (var s in _sinks) s.OnPitch(t); }
    public void OnPlateAppearance(PaTrace t) { foreach (var s in _sinks) s.OnPlateAppearance(t); }
    public void OnGameEnd(GameResult result) { foreach (var s in _sinks) s.OnGameEnd(result); }
}

using System.Collections.Generic;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;

namespace KokoSim.Engine.Match.AtBat;

/// <summary>打席の詳細結果（結果＋投球数）。試合時間推定・スタミナ消費に使う。</summary>
/// <param name="Result">打席結果。</param>
/// <param name="Pitches">投球数。</param>
public sealed record AtBatResult(PlateAppearanceResult Result, int Pitches)
{
    /// <summary>
    /// プレーのタイムライン（CHANGELOG 32）。AtBatContext.CaptureTimeline が真のときのみ構築される
    /// （統計シミュレーションではゼロコスト）。インプレー打球（安打/凡打/失策/本塁打）で非null。
    /// </summary>
    public PlayTimeline? Timeline { get; init; }

    /// <summary>
    /// 守備解決の幾何（設計書12 §2.2, F1）。CaptureTimeline 時のみ非null＝統計シムはゼロコスト。
    /// GameEngine が塁状況確定後に併殺の送球連鎖などを組むため、処理野手・捕球点・送球時刻を持ち回す。
    /// </summary>
    public FieldingPlay? Play { get; init; }

    /// <summary>
    /// この打席で実際に解いた1球ごとの記録（設計書15 §4）。観測データ＝試合結果に影響しない。
    /// バント/スクイズ等 <see cref="AtBatSession"/> を通らない経路では null（統一は Phase D）。
    /// </summary>
    public IReadOnlyList<PitchRecord>? PitchLog { get; init; }

    /// <summary>
    /// バント試行で確定した打席の詳細結果（設計書15 Phase D-2b）。null=バント経路ではない
    /// （<see cref="Result"/> だけでは PopOut と SacrificeSuccess が同じ InPlayOut に潰れて区別できないため、
    /// GameEngine 側の進塁処理（AdvanceOnBunt を呼ぶか/走者を釘付けにするか）の分岐に使う）。
    /// </summary>
    public BuntResult? BuntOutcome { get; init; }

    /// <summary>
    /// スクイズ試行で確定した打席の詳細結果（設計書15 Phase D-2c）。null=スクイズ経路ではない。
    /// ウエストを読まれて三塁走者だけが挟殺された場合はここではなく打席継続のまま
    /// <see cref="PitchResolution.SqueezeRunnerCaughtAtThird"/> で通知される（打席は確定しない）。
    /// </summary>
    public SqueezeOutcome? Squeeze { get; init; }
}

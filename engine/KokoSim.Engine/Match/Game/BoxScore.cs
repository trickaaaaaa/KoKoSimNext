using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 個人打撃成績（1試合）。プレー記録から集計。HitByPitches/StolenBases/CaughtStealing は SourceId より後
/// （互換のため末尾追加）。Position は表示専用＝DHスロットは <see cref="FieldPosition.DesignatedHitter"/>（issue #70）。
/// </summary>
public sealed record BattingLine(
    int Order, FieldPosition Position, string Name,
    int PlateAppearances, int AtBats, int Hits, int Doubles, int Triples, int HomeRuns,
    int Rbi, int Walks, int StrikeOuts, int? SourceId = null, int HitByPitches = 0,
    int StolenBases = 0, int CaughtStealing = 0, int Runs = 0, int SacrificeFlies = 0)
{
    /// <summary>打率（打数0なら0）。</summary>
    public double Average => AtBats > 0 ? (double)Hits / AtBats : 0.0;
}

/// <summary>
/// 個人投手成績（1試合）。SourceId＝育成選手ID（相手校の生成投手は null）。HitBatters＝与死球。
/// UniformNumber は SourceId/HitBatters より後（互換のため末尾追加）。相手校投手は SourceId が
/// 常に null のため、大会を跨いだ同一投手の同定（#41 TournamentPitchLedger）に背番号を使う。
/// </summary>
public sealed record PitchingLine(
    string Name, int Outs, int BattersFaced, int Hits, int Runs, int StrikeOuts, int Walks, int Pitches,
    int? SourceId = null, int HitBatters = 0, int UniformNumber = 0, int HomeRunsAllowed = 0,
    IReadOnlyDictionary<PitchType, PitchTypeBattingLine>? BattingAgainstByPitch = null)
{
    /// <summary>投球回（アウト数/3）。例: 7回1/3 → "7 1/3"。</summary>
    public string InningsText => (Outs / 3) + (Outs % 3 == 0 ? "" : " " + (Outs % 3) + "/3");
}

/// <summary>個人守備成績（1試合）。現状は失策数のみ（issue #91）。SourceId＝育成選手ID（相手校の生成選手は null）。</summary>
public sealed record FieldingLine(int? SourceId, FieldPosition Position, string Name, int Errors);

/// <summary>
/// 球種ごとの被打成績（issue #180）。打席確定球（インプレー/被安打を生んだ球）の球種で集計する
/// ＝三振・四球・死球は対象外（被打数は「対戦打数相当」であり公式打数とは一致しない）。
/// </summary>
public sealed record PitchTypeBattingLine(int AtBats, int Hits, int HomeRuns)
{
    /// <summary>被打率（対象打数0なら0）。</summary>
    public double Average => AtBats > 0 ? (double)Hits / AtBats : 0.0;
}
